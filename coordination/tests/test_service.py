import concurrent.futures
import hashlib
import http.client
import json
import sys
import tempfile
import threading
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from service import ApiError, LEASE, Server, Store


class SelectionTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.now = 100.0
        self.store = Store(Path(self.temp.name) / "state.db", clock=lambda: self.now)
        self.a = self.join(self.store.create_group()["code"], "Mac A")
        self.b = self.join(self.call(self.a, "/v1/pair-code")["code"], "Windows")
        self.now += LEASE

    def tearDown(self):
        self.store.close()
        self.temp.cleanup()

    def join(self, code, name):
        return self.store.request("/v1/join", {"code": code, "name": name})

    def call(self, device, path, body=None):
        return self.store.request(path, body or {}, device["token"])

    def beat(self, device, age=0, sequence=1, available=True, account="a" * 64, runtime=None):
        return self.call(device, "/v1/heartbeat", dict(
            runtimeId=runtime or device["deviceId"], inputSequence=sequence, inputAge=age,
            available=available, accountKey=account))

    def test_only_one_publisher_and_handoff_waits_for_old_grant(self):
        self.assertTrue(self.beat(self.a)["allowed"])
        self.now += 3
        self.assertFalse(self.beat(self.b)["allowed"])
        self.now += 2
        self.assertFalse(self.beat(self.b, age=2)["allowed"])
        self.assertFalse(self.beat(self.a, age=5)["allowed"])
        self.now += 10
        self.assertTrue(self.beat(self.b, age=12)["allowed"])
        self.assertFalse(self.beat(self.a, age=15)["allowed"])

    def test_heartbeats_do_not_count_as_input(self):
        self.beat(self.a, age=4)
        self.now += 3
        self.beat(self.b, age=0)
        self.now += 3
        # Even a broken age measurement with an unchanged sequence cannot steal back.
        self.assertFalse(self.beat(self.a, age=0)["allowed"])
        self.now += 10
        self.assertTrue(self.beat(self.b, age=13)["allowed"])

    def test_near_ties_keep_current_computer(self):
        self.beat(self.a)
        self.now += .5
        self.assertFalse(self.beat(self.b)["allowed"])
        self.now += 3
        self.assertTrue(self.beat(self.a, age=3.5)["allowed"])

    def test_idle_or_unavailable_never_granted(self):
        self.assertFalse(self.beat(self.a, age=300)["allowed"])
        self.assertFalse(self.beat(self.b, available=False)["allowed"])

    def test_different_account_is_rejected(self):
        self.beat(self.a)
        with self.assertRaises(ApiError) as result:
            self.beat(self.b, account="b" * 64)
        self.assertEqual(result.exception.code, "different_discord_account")

    def test_no_grant_without_account(self):
        self.assertFalse(self.beat(self.a, account="")["allowed"])

    def test_recent_computer_with_discord_closed_hides_unattended_peer(self):
        self.assertTrue(self.beat(self.a)["allowed"])
        self.now += 3
        self.beat(self.b, account="")
        self.now += 2
        result = self.beat(self.b, account="", age=2)
        self.assertFalse(result["allowed"])
        self.assertTrue(result["selectedHere"])
        self.assertFalse(self.beat(self.a, age=5)["allowed"])

    def test_new_runtime_cannot_reuse_old_runtime_grant(self):
        first = self.beat(self.a)
        with self.assertRaises(ApiError):
            self.beat(self.a, runtime="another_runtime_123")
        self.now += LEASE
        second = self.beat(self.a, runtime="another_runtime_123")
        self.assertTrue(second["allowed"])
        self.assertGreater(second["generation"], first["generation"])

    def test_startup_quarantine_and_persistent_generation(self):
        first = self.beat(self.a)
        path = Path(self.temp.name) / "state.db"
        self.store.close()
        self.store = Store(path, clock=lambda: self.now)
        self.assertFalse(self.beat(self.b)["allowed"])
        self.now += LEASE
        result = self.beat(self.b, age=LEASE)
        self.assertTrue(result["allowed"])
        self.assertGreater(result["generation"], first["generation"])

    def test_revocation_does_not_free_unexpired_grant(self):
        self.beat(self.a)
        self.call(self.b, "/v1/revoke", {"deviceId": self.a["deviceId"]})
        with self.assertRaises(ApiError):
            self.beat(self.a)
        self.assertFalse(self.beat(self.b)["allowed"])
        self.now += LEASE
        self.assertTrue(self.beat(self.b, age=LEASE)["allowed"])

    def test_pairing_code_is_single_use_and_expires(self):
        code = self.call(self.a, "/v1/pair-code")["code"]
        self.join(code, "Mac B")
        with self.assertRaises(ApiError):
            self.join(code, "Imposter")
        code = self.call(self.a, "/v1/pair-code")["code"]
        self.now += 300
        with self.assertRaises(ApiError):
            self.join(code, "Expired")

    def test_pairing_is_explicit_and_does_not_cross_groups(self):
        stranger = self.join(self.store.create_group()["code"], "Other person")
        self.beat(self.a)
        self.assertTrue(self.beat(stranger)["allowed"])
        self.call(stranger, "/v1/revoke", {"deviceId": self.a["deviceId"]})
        self.assertTrue(self.beat(self.a)["allowed"])

    def test_unknown_content_fields_and_invalid_numbers_rejected(self):
        with self.assertRaises(ApiError):
            self.call(self.a, "/v1/heartbeat", {"conversation": "private"})
        for age in (-1, float("inf"), float("nan"), "2", True):
            with self.subTest(age=age), self.assertRaises(ApiError):
                self.beat(self.a, age=age)

    def test_old_sequence_rejected(self):
        self.beat(self.a, sequence=2)
        with self.assertRaises(ApiError):
            self.beat(self.a, sequence=1)

    def test_concurrent_claims_never_grant_two_publishers(self):
        with concurrent.futures.ThreadPoolExecutor(2) as pool:
            results = list(pool.map(self.beat, (self.a, self.b)))
        self.assertEqual(sum(r["allowed"] for r in results), 1)

    def test_tokens_hashed_in_database(self):
        data = self.store.db.execute("SELECT secret FROM devices WHERE id=?", (self.a["deviceId"],)).fetchone()[0]
        self.assertEqual(data, hashlib.sha256(self.a["token"].encode()).hexdigest())
        self.assertNotEqual(data, self.a["token"])


class HttpTests(unittest.TestCase):
    def test_end_to_end_join_and_bounded_private_protocol(self):
        with tempfile.TemporaryDirectory() as tmp:
            store = Store(Path(tmp) / "state.db")
            code = store.create_group()["code"]
            server = Server(("127.0.0.1", 0), store)
            thread = threading.Thread(target=server.serve_forever, daemon=True)
            thread.start()
            try:
                def post(path, body, headers=None):
                    client = http.client.HTTPConnection(*server.server_address, timeout=3)
                    client.request("POST", path, body, headers or {"Content-Type": "application/json"})
                    response = client.getresponse()
                    result = response.status, response.getheader("Cache-Control"), json.loads(response.read())
                    client.close()
                    return result
                status, cache, joined = post("/v1/join", json.dumps({"code": code, "name": "Mac"}))
                self.assertEqual((status, cache), (200, "no-store"))
                self.assertIn("token", joined)
                self.assertEqual(post("/v1/join", " " * 5000)[0], 413)
                self.assertEqual(post("/v1/pair-code", "{}")[0], 401)
                self.assertEqual(post("/v1/join", '{"code":NaN,"name":"Mac"}')[0], 400)
            finally:
                server.shutdown()
                server.server_close()
                thread.join()
                store.close()


if __name__ == "__main__":
    unittest.main()

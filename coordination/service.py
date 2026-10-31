"""Private, dependency-free device selection. Run behind HTTPS; see README.md.

No Discord/T3 content is accepted. A single SQLite writer serializes grants.
Only one process may serve a database (the lifetime file lock enforces this).
"""
import argparse
import base64
import hashlib
import json
import math
import os
import re
import secrets
import sqlite3
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

LEASE = 15.0
SETTLE = 2.0
IDLE = 300.0
FRESH = 10.0
MAX_BODY = 4096


class ApiError(Exception):
    def __init__(self, status, code):
        self.status, self.code = status, code


def digest(value):
    return hashlib.sha256(value.encode()).hexdigest()


def identifier(value):
    return isinstance(value, str) and re.fullmatch(r"[a-zA-Z0-9_-]{16,128}", value)


class Store:
    def __init__(self, path, clock=time.monotonic):
        self.clock = clock
        self.lock = threading.RLock()
        self.db = sqlite3.connect(path, check_same_thread=False, isolation_level=None)
        self.db.row_factory = sqlite3.Row
        self.db.executescript("""
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS groups (
                id TEXT PRIMARY KEY, account TEXT, generation INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS devices (
                id TEXT PRIMARY KEY, group_id TEXT NOT NULL, secret TEXT UNIQUE NOT NULL,
                name TEXT NOT NULL, revoked INTEGER NOT NULL DEFAULT 0
            );
        """)
        # Runtime observations are deliberately not restored across process/clock restarts.
        # The coordinator must wait out every possible old grant before issuing another.
        self.quarantine_until = self.clock() + LEASE
        self.observations = {}
        self.grants = {}
        self.challenges = {}
        self.codes = {}
        self.failures = {}

    def close(self):
        self.db.close()

    def create_group(self):
        group = secrets.token_urlsafe(24)
        with self.lock:
            self.db.execute("INSERT INTO groups(id) VALUES (?)", (group,))
            return self.make_code(group)

    def make_code(self, group):
        now = self.clock()
        self.codes = {k: v for k, v in self.codes.items() if v[1] > now}
        # At most one pending code per group, with no unbounded allocation.
        self.codes = {k: v for k, v in self.codes.items() if v[0] != group}
        code = base64.b32encode(secrets.token_bytes(10)).decode().rstrip("=")
        self.codes[digest(code)] = (group, now + 300)
        return {"code": code, "expiresIn": 300}

    def authenticate(self, token):
        if not identifier(token):
            raise ApiError(401, "not_linked")
        device = self.db.execute(
            "SELECT * FROM devices WHERE secret=? AND revoked=0", (digest(token),)
        ).fetchone()
        if device is None:
            raise ApiError(401, "not_linked")
        return device

    def join(self, body, source):
        now = self.clock()
        # Bounded, global + per-source limits. Trust no forwarded IP header.
        self.failures = {k: v for k, v in self.failures.items() if now - v[0] < 300}
        for key in ("*", source):
            since, count = self.failures.get(key, (now, 0))
            if count >= (60 if key == "*" else 10):
                raise ApiError(429, "try_later")
            self.failures[key] = (since, count + 1)
        code, name = body.get("code"), body.get("name")
        if not isinstance(code, str) or not isinstance(name, str):
            raise ApiError(400, "invalid_request")
        code = code.replace("-", "").replace(" ", "").upper()
        if not re.fullmatch(r"[A-Z2-7]{16}", code):
            raise ApiError(400, "invalid_code")
        if not name.strip() or len(name) > 48 or any(ord(c) < 32 for c in name):
            raise ApiError(400, "invalid_name")
        invite = self.codes.get(digest(code))
        if invite is None or invite[1] <= now:
            raise ApiError(400, "invalid_code")
        group = invite[0]
        count = self.db.execute("SELECT count(*) FROM devices WHERE group_id=? AND revoked=0", (group,)).fetchone()[0]
        if count >= 16:
            raise ApiError(409, "too_many_computers")
        token, device_id = secrets.token_urlsafe(32), secrets.token_urlsafe(24)
        self.db.execute("INSERT INTO devices(id,group_id,secret,name) VALUES (?,?,?,?)",
                        (device_id, group, digest(token), name.strip()))
        del self.codes[digest(code)]
        return {"deviceId": device_id, "groupId": group, "token": token}

    def heartbeat(self, device, body):
        now = self.clock()
        runtime, sequence = body.get("runtimeId"), body.get("inputSequence")
        age, available, account = body.get("inputAge"), body.get("available"), body.get("accountKey")
        if not identifier(runtime) or type(sequence) is not int or not 0 <= sequence < 2**53:
            raise ApiError(400, "invalid_request")
        if type(age) not in (int, float) or not math.isfinite(age) or not 0 <= age <= 604800:
            raise ApiError(400, "invalid_request")
        if type(available) is not bool or not isinstance(account, str) or not re.fullmatch(r"[a-f0-9]{64}|", account):
            raise ApiError(400, "invalid_request")
        group, device_id = device["group_id"], device["id"]
        g = self.db.execute("SELECT * FROM groups WHERE id=?", (group,)).fetchone()
        if account and g["account"] and g["account"] != account:
            if device_id in self.observations:
                self.observations[device_id]["available"] = False
            # No activity, lease or private device names are returned to a different account.
            raise ApiError(409, "different_discord_account")
        if account and not g["account"]:
            self.db.execute("UPDATE groups SET account=? WHERE id=?", (account, group))
        old = self.observations.get(device_id)
        # Another live instance of the same device must not renew/replace its runtime.
        if old and old["runtime"] != runtime and now - old["seen"] < LEASE:
            raise ApiError(409, "another_copy")
        if old and old["runtime"] == runtime:
            if sequence < old["sequence"]:
                raise ApiError(409, "old_observation")
            last_input = old["input"] if sequence == old["sequence"] else now - age
        else:
            last_input = now - age
        self.observations[device_id] = dict(group=group, runtime=runtime, sequence=sequence,
                                           input=last_input, seen=now, available=available,
                                           name=device["name"], account=account)
        bound_account = g["account"] or account
        candidates = {key: value for key, value in self.observations.items()
                      if bound_account and value["group"] == group and value["available"]
                      and now - value["seen"] < FRESH and now - value["input"] < IDLE}
        grant = self.grants.get(group)
        incumbent = grant["device"] if grant else None
        selected = incumbent if incumbent in candidates else None
        # Retain the incumbent for near ties (one second of network uncertainty).
        best = max(candidates, key=lambda key: (candidates[key]["input"], key), default=None)
        if best and (selected is None or candidates[best]["input"] > candidates[selected]["input"] + 1):
            challenge = self.challenges.get(group)
            if selected is None:
                selected = best
            elif challenge and challenge[0] == best and now - challenge[1] >= SETTLE:
                selected = best
            elif not challenge or challenge[0] != best:
                self.challenges[group] = (best, now)
        else:
            self.challenges.pop(group, None)
        # Expired grants stay reserved until their deadline. No early release, so a
        # delayed clear/response cannot race a new publisher's connection.
        if grant and grant["expires"] <= now:
            self.grants.pop(group)
            grant = None
        if account and now >= self.quarantine_until and selected == device_id:
            if grant is None:
                self.db.execute("UPDATE groups SET generation=generation+1 WHERE id=?", (group,))
                generation = self.db.execute("SELECT generation FROM groups WHERE id=?", (group,)).fetchone()[0]
                grant = dict(device=device_id, runtime=runtime, generation=generation, expires=now + LEASE)
                self.grants[group] = grant
            elif grant["device"] == device_id and grant["runtime"] == runtime:
                grant["expires"] = now + LEASE
        allowed = bool(account and grant and selected == device_id and grant["device"] == device_id
                       and grant["runtime"] == runtime and now >= self.quarantine_until)
        return {"allowed": allowed, "generation": grant["generation"] if allowed else 0,
                "validFor": LEASE if allowed else 0,
                "selectedName": candidates[selected]["name"] if selected else None,
                "selectedHere": selected == device_id,
                "switching": selected is not None and bool(candidates[selected]["account"])
                             and not (grant and grant["device"] == selected),
                "retryAfter": 3}

    def request(self, path, body, token="", source="local"):
        # Unknown fields are rejected: callers cannot accidentally upload activity text.
        schemas = {"/v1/join": {"code", "name"}, "/v1/heartbeat": {
            "runtimeId", "inputSequence", "inputAge", "available", "accountKey"},
            "/v1/pair-code": set(), "/v1/leave": set(), "/v1/revoke": {"deviceId"},
            "/v1/computers": set()}
        if path not in schemas:
            raise ApiError(404, "not_found")
        if not isinstance(body, dict) or set(body) != schemas[path]:
            raise ApiError(400, "invalid_request")
        with self.lock:
            if path == "/v1/join":
                return self.join(body, source)
            device = self.authenticate(token)
            if path == "/v1/heartbeat":
                return self.heartbeat(device, body)
            if path == "/v1/pair-code":
                return self.make_code(device["group_id"])
            if path == "/v1/computers":
                rows = self.db.execute("SELECT id,name FROM devices WHERE group_id=? AND revoked=0", (device["group_id"],))
                return {"computers": [{"id": r["id"], "name": r["name"]} for r in rows]}
            target = device["id"] if path == "/v1/leave" else body.get("deviceId")
            if not identifier(target):
                raise ApiError(400, "invalid_request")
            self.db.execute("UPDATE devices SET revoked=1 WHERE id=? AND group_id=?", (target, device["group_id"]))
            self.observations.pop(target, None)
            # Do not discard an outstanding grant before its original expiry.
            return {"removed": True}


class Handler(BaseHTTPRequestHandler):
    server_version = "DeveloperPresence/1"
    protocol_version = "HTTP/1.0"

    def log_message(self, *_):
        pass  # Pairing codes, auth headers, names and request bodies are never logged.

    def setup(self):
        super().setup()
        self.connection.settimeout(5)

    def do_POST(self):
        try:
            size = self.headers.get("Content-Length", "")
            if not size.isdecimal() or not 0 < int(size) <= MAX_BODY or self.headers.get("Transfer-Encoding"):
                raise ApiError(413, "invalid_size")
            if self.headers.get_content_type() != "application/json":
                raise ApiError(415, "json_required")
            raw = self.rfile.read(int(size))
            if len(raw) != int(size):
                raise ApiError(400, "invalid_request")
            body = json.loads(raw, parse_constant=lambda _: (_ for _ in ()).throw(ValueError()))
            auth = self.headers.get("Authorization", "")
            token = auth[7:] if auth.startswith("Bearer ") else ""
            result = self.server.store.request(self.path, body, token, self.client_address[0])
            self.reply(200, result)
        except ApiError as error:
            self.reply(error.status, {"error": error.code})
        except (ValueError, UnicodeError, TimeoutError):
            self.reply(400, {"error": "invalid_request"})
        except Exception:
            self.reply(500, {"error": "unavailable"})

    def reply(self, status, body):
        data = json.dumps(body, separators=(",", ":")).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.send_header("Cache-Control", "no-store")
        self.send_header("X-Content-Type-Options", "nosniff")
        self.end_headers()
        try:
            self.wfile.write(data)
        except (BrokenPipeError, ConnectionError):
            pass


class Server(ThreadingHTTPServer):
    daemon_threads = True

    def __init__(self, address, store):
        self.store = store
        self.slots = threading.BoundedSemaphore(32)
        super().__init__(address, Handler)

    def process_request(self, request, address):
        if not self.slots.acquire(blocking=False):
            request.close()
            return
        try:
            super().process_request(request, address)
        except Exception:
            self.slots.release()
            raise

    def process_request_thread(self, request, address):
        try:
            super().process_request_thread(request, address)
        finally:
            self.slots.release()


def main():
    parser = argparse.ArgumentParser(description="Private Developer Presence computer linking")
    parser.add_argument("--database", required=True)
    parser.add_argument("--port", type=int, default=4859)
    parser.add_argument("--first-code-file", help="Write a private first-computer code if no computers are linked")
    args = parser.parse_args()
    os.umask(0o077)
    path = Path(args.database).expanduser().resolve()
    path.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
    # Prevent two processes with separate monotonic grant tables serving the same DB.
    import fcntl
    with open(str(path) + ".lock", "a") as lock:
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        store = Store(path)
        server = Server(("127.0.0.1", args.port), store)
        if args.first_code_file and not store.db.execute("SELECT 1 FROM devices WHERE revoked=0 LIMIT 1").fetchone():
            code_path = Path(args.first_code_file).expanduser()
            fd = os.open(code_path, os.O_WRONLY | os.O_CREAT | os.O_TRUNC | os.O_NOFOLLOW, 0o600)
            os.fchmod(fd, 0o600)
            with os.fdopen(fd, "w") as code_file:
                code_file.write(store.create_group()["code"] + "\n")
            print("First-computer code written to the private file; it expires in five minutes.", flush=True)
        print("Listening on localhost. Put this private service behind HTTPS.", flush=True)
        # Console enrollment is intentional. No unauthenticated public group-creation API.
        def console():
            try:
                for line in iter(input, None):
                    if line.strip() == "pair":
                        print("First computer code:", store.create_group()["code"], flush=True)
            except EOFError:
                pass
        threading.Thread(target=console, daemon=True).start()
        try:
            server.serve_forever()
        finally:
            server.server_close()
            store.close()


if __name__ == "__main__":
    main()

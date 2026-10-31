"""Actual C# client services against the real Python HTTP server; no Discord account.

Only OS input is simulated. Handoff uses the real fifteen-second grant expiry.
The separate unit suite covers startup quarantine, so this fixture starts after it.
"""
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import threading

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "coordination"))
from service import Server, Store


def main():
    with tempfile.TemporaryDirectory(prefix="presence-http-") as directory:
        store = Store(Path(directory) / "test.sqlite3")
        store.quarantine_until = 0
        code = store.create_group()["code"]
        server = Server(("127.0.0.1", 0), store)
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        env = dict(os.environ, TEST_LINK_ADDRESS=f"http://127.0.0.1:{server.server_port}", TEST_LINK_CODE=code)
        try:
            result = subprocess.run(
                ["dotnet", "run", "--project", str(ROOT / "checks/ComputerLink.Integration/ComputerLink.Integration.csproj")],
                env=env, timeout=90, capture_output=True, text=True)
            print(result.stdout, end="")
            if result.returncode:
                print(result.stderr, end="", file=sys.stderr)
            return result.returncode
        finally:
            server.shutdown()
            server.server_close()
            thread.join()
            store.close()


if __name__ == "__main__":
    sys.exit(main())

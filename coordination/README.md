# Private computer linking service

This small Python 3 service lets the macOS and Windows companions agree on one computer to share from. It receives computer names, coarse recent-input timing, availability, and a group-specific Discord account fingerprint. It does not receive app names, agent tools, counts, T3 addresses, or conversations. There are no third-party Python dependencies.

This is a self-hosted development service. No public service is bundled or deployed. Run one instance behind authenticated private-network HTTPS, such as a Tailscale connection shared only with your own computers. The companions require HTTPS except for localhost testing. The server itself binds only to `127.0.0.1`; never expose its HTTP port directly to the Internet. A public deployment would need additional operational abuse protection, monitoring, recovery, and a privacy policy.

## First setup

Use Python 3.10 or later on a Unix host with a persistent local disk. The service's process lock uses `fcntl`, so it is not a native Windows server. The desktop apps remain native on Windows and macOS.

On the Mumbai VPS, first verify `/data` is mounted. Keep state under `/data/ubuntu`, private to the service owner. For example:

```sh
mountpoint -q /data
install -d -m 700 /data/ubuntu/developer-presence-state
python3 /data/ubuntu/projects/developer-presence/coordination/service.py \
  --database /data/ubuntu/developer-presence-state/computers.sqlite3 \
  --first-code-file /data/ubuntu/developer-presence-state/first-computer-code
```

Keep this process running. Configure a private HTTPS reverse proxy to `http://127.0.0.1:4859`, using a new, dedicated address/port; do not replace the existing T3, Paperclip, or other routes. Request bodies and Authorization headers must not be logged by the proxy. Configure a bounded request-body size and timeout there as well. Do not set up several independently running replicas or load-balance separate copies of the database.

The first code is written to the specified owner-only file only when no active computers are linked. Read it in your own terminal, not in chat. It expires five minutes after server startup and works once. Restart before your first link if it expires. With an interactive terminal, typing `pair` and Enter creates a fresh group/code; this is an administrative action, not how you add a computer to an existing group. Do not record that terminal's output.

1. Open Developer Presence settings → **Computers → Follow me**.
2. Enter your private HTTPS **Linking address**, a name for this computer, and the first code.
3. Choose **Link this computer**. It saves a private device credential in Keychain or Windows Credential Manager.
4. On this linked computer, choose **Link another computer**. Enter that new code and the same linking address on the next computer. Repeat for each Mac or Windows PC; each code works once.
5. Use the same Discord account on all linked computers, set the same Discord application ID, and enable sharing on each computer you want to participate. Linking alone never turns sharing on.

After the first computer is linked, its **Link another computer** button creates subsequent codes. A group supports up to 16 linked computers. Enrollment attempts are rate limited, codes have 80 random bits, and the server stores device token hashes rather than the tokens. Only linked devices can create another code or list/revoke group members.

## What to expect

The service samples reports every three seconds. A more recent computer must stay ahead for two seconds, with one second of uncertainty retaining the current computer. The previous computer's permission is reserved for up to 15 seconds; its app stops using that permission three seconds early. A handoff can therefore take roughly 20 seconds plus network delay. It deliberately waits rather than having two computers send conflicting updates.

Input comes from the OS idle timer. The app does not record keystrokes or pointer locations. Agent work, polling, background updates, and starting the app are not treated as new input. Remote desktop and input-sharing software can deliver input to more than one computer; near ties keep the existing selection. This observes OS input, not a proof of where a human is physically sitting.

Using an unsupported app on the selected computer hides the developer activity. It does not select an unattended computer where T3 remains open. Five minutes without input hides activity. Pause/off/lock/sleep excludes that computer. Pause is local; it does not pause the entire group. The app publishes only its selected computer's counts, even if several computers observe the same T3 environment.

If the linking service is unavailable, linked apps clear their activity rather than falling back to independent publishing. A service restart waits 15 seconds before granting anyone permission, so an old grant cannot overlap a new one. The database preserves membership, account binding, and increasing grant generations; transient input observations and grants are intentionally not restored. The service must have one authoritative process/database. Do not restore a live state copy into a parallel server.

Discord account fingerprints bind a group to the first account reported by a linked desktop. Signing into another Discord account is blocked; it does not move the group automatically. Missing READY account identity is also blocked. The app uses Discord's existing local handshake, not a Discord password, account token, or bot.

Choosing **Unlink this computer** turns off sharing locally, revokes that device at the service, and removes the saved credential. If the service is unreachable, sharing stays off and unlinking asks you to retry. Returning to independent mode does not establish precedence over other installations; leave sharing off unless you intend to use only that computer. Old unlinked builds or other Discord extensions do not participate in this protocol.

## State and administration

Device names, group-specific account fingerprints, token hashes, and revocation flags persist in the owner-only SQLite database; active timing observations remain in memory. Request bodies and credentials are not logged. There is no automatic history of input events. Persistent membership is retained until the host removes the group/database; revoked records remain for administration. The first-code file can be deleted after enrollment.

The authenticated JSON endpoints `/v1/computers` and `/v1/revoke` let an administrator list the current group and remove a lost computer. Both are POST requests with the linked device's Bearer credential; revoke takes `{"deviceId":"…"}`. Use a trusted local administrative client that reads credentials from secure storage; do not paste device credentials into chat, shell history, URLs, or documentation. The first version does not include a device-list/revocation screen. If all devices are lost, stop the service, archive or remove the private group state as appropriate, and enroll a fresh group. Old clients must not be allowed to reach a parallel copy of the old server.

Do not copy or back up the live SQLite files directly. Stop this service before a filesystem copy, or use SQLite's online backup API. Protect backups like the active state. Restore onto one server and allow its startup quarantine to complete.

## Validation

```sh
python3 -m unittest discover -s coordination/tests -v
python3 coordination/tests/native_http.py
```

The suite exercises mixed-computer selection, ties, simultaneous claims, old runtimes, unchanged input, idle/unavailable devices, expired codes, revocation, separate groups/accounts, restart quarantine, hashed tokens, invalid content, and a real loopback HTTP exchange. The second command needs .NET 8 and tests two actual C# client services against the Python server, with simulated input and real grant expiry. It verifies enrollment, an idle peer staying quiet, the old client stopping, and exclusive takeover after expiry. Native permission and coordinator tests cover the publishing gates. Real Mac/Windows/Discord acceptance is still required: these tests do not prove the order of profile updates inside Discord. See [the acceptance contract](../spec/multiple-computers.md#release-acceptance).

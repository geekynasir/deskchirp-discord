# Sharing from multiple computers

Status: **Follow me selected by the user on 2026-10-31.** Development implementation added in `coordination/` and both native apps. Native builds and real multi-computer Discord acceptance must be recorded before release. No shared service is deployed by these changes. Manual selection is a future option, not implemented.

## Current behavior and Discord's boundary

Each installed companion connects to Discord on its own computer. Earlier builds, through `1c4e4c1`, checked only local settings, active app, and lock/sleep state. New linked mode adds OS input age, a shared selection, and expiring publishing grants. Independent mode retains its original local-only behavior.

Discord's documented [`SET_ACTIVITY`](https://github.com/discord/discord-api-docs/blob/main/developers/topics/rpc.mdx#setactivity) arguments identify a local process and its activity. They do not expose a cross-computer priority, exclusive publishing claim, or conditional update tied to a previous owner. The [IPC reference](https://github.com/discord/discord-rpc/blob/master/documentation/hard-mode.md) also describes local connections, not selection between computers. No predictable cross-computer precedence is established by these references. Do not describe Discord's behavior as guaranteed last-update-wins, prefer Windows, or prefer macOS.

The app can coordinate its own participating installations. It cannot control Discord's own game detection, another presence extension, an old unpaired companion, or the order in which Discord propagates already accepted updates. Real Discord tests must establish visible convergence separately from the app's publishing permissions.

## User-facing choices

The **Computers** section implements Follow me. A possible future manual option is described here for context:

- **Follow me**: use the linked computer with the most recent observed input, rather than whichever sends an update last or has the most running agents.
- **Use one computer**: keep the selection on a computer the user chooses until they change it. If that computer becomes unavailable, hide activity instead of silently selecting another one.

**Use this computer** is a future explicit switch, not part of this implementation. A device name is chosen at enrollment, visible only in private settings, and never sent as part of Discord activity. Linking uses a short-lived, single-use approval code that the user intentionally enters on the new computer. A public Discord application ID is shared product identity, not evidence that two installations belong to the same person. Never pair computers by that ID, a hostname, a local network, or a T3 address.

Suggested states: **Sharing from this computer**, **Sharing from {computer}**, **Switching computers…**, **Waiting for a connection**, and **No computer is sharing**. Until real Discord acceptance is known, say **Selected computer** rather than claiming another computer has visibly updated the profile.

## Selection rules

1. All participating computers must be explicitly linked to the same private device group and intended Discord account. Linking does not enable Discord sharing or T3 activity by itself. Different Discord accounts must not silently compete for the same selection; changing the local Discord account suspends participation pending confirmation. Account verification and local Discord channel selection must be implemented before this can be claimed.
2. Recent input means an OS-provided last-input age, read without recording keystrokes, mouse coordinates, window titles, or screen contents. Focus changes, T3 poll results, workload size, service heartbeats, Discord reconnects, and booting a computer are not new user input. Input age must be refreshed periodically even if the same app stays active.
3. In Follow me mode, recent input on a linked, opted-in, unlocked, awake computer selects that computer even when the active app is unsupported or Discord is closed. An old PC with T3 left open must not take over while the user is browsing on a Mac. An unsupported app or closed Discord on the selected computer hides activity; it does not cause a fallback to another computer's stale supported app. A computer without verified Discord identity may suppress sharing in an already account-bound group, but cannot receive a publishing grant.
4. Compare input recency without relying on synchronized wall clocks. Reject stale observations and invalid ages. Ties or overlapping uncertainty retain the current selection. A challenger must remain more recent for a proposed two-second settling period; an explicit Use this computer action bypasses that settling period. These are initial policy values, not measured latency guarantees.
5. Only the selected computer may publish, and only when its existing local sharing/privacy gates also pass. This applies to app-only activity as well as T3 counts. Do not add task/helper counts across computers: several computers may observe the same remote T3 environment.
6. Lock, sleep, quit, local pause, sharing off, Discord disconnect, or a failed input observation immediately invalidates the local right to publish. A different computer may take over in Follow me mode only with fresh input evidence; merely staying open is insufficient. Wake/unlock or reconnect requires fresh observations and a new publishing grant. Restored cached state is never permission to send.
7. A five-minute input-idle timeout hides activity when every linked computer is idle. It does not display an Idle card or infer that the user has stopped working. This is disclosed in linked-mode settings; independent mode retains the focus-only baseline.
8. Existing Pause sharing and Share on Discord remain local controls and must be labeled/documented as such in the Computers area. A future Pause on all computers control would require a separate, durable group setting; it must not be simulated by pausing only the current computer. A globally selected computer does not override any local opt-out.

## Coordination and safe handoff

Independent local Discord connections cannot enforce the selection on their own. A shared, authenticated coordinator is required for linked mode, reachable across networks, with atomic ownership decisions. The dependency-free Python implementation uses one SQLite-backed authority and private device credentials. [Hosting and recovery instructions](../coordination/README.md) describe private HTTPS setup. No new service is deployed by this change; the existing T3 read-only interface is not a place to write device locks.

Use short-lived publishing grants with increasing generations. Clients report every three seconds; each server grant lasts at most fifteen seconds, and clients stop using it three seconds early. The client computes a conservative local monotonic deadline from the request start, not from receipt of a delayed response. If a grant cannot be renewed, clear and disconnect before its deadline; never keep publishing offline on a remembered grant. Recheck the grant immediately before every queued publish and invalidate delayed callbacks on selection changes. A standby connection may perform a local Discord handshake to verify account identity, but may not publish an activity.

For a handoff, stop renewing the former computer, have that client cancel its queued work, clear its own local activity and close its connection, and reserve its old permission until the full fifteen-second server grant expires. There is deliberately no early-release optimization in this implementation. No device may clear another device's activity. Test delayed clear/update ordering on real Discord clients, since local acknowledgment is not proof of global display order.

The coordinator serializes simultaneous claims and persists enough state to survive restart without issuing overlapping grants. If it loses authoritative state, wait out the maximum possible old grant before selecting anyone. Clients must handle process suspension, clock changes, network partitions, late renewals, reconnects, revocation, and server failover. A client that resumes after its deadline must close any stale Discord connection before attempting to acquire permission again.

This prevents authorized clients from intentionally publishing concurrently; it cannot fence a command already accepted by Discord. A suspended old client or delayed Discord propagation may leave a card visible temporarily. Do not promise instantaneous switching or a strict global display guarantee unsupported by Discord. Automatic switching remains a release gate until these cases are tested.

Linked mode fails closed when coordination is unavailable: show Waiting for a connection and stop sharing once any current grant expires. Do not silently fall back to standalone publishing. Standalone mode remains available for one computer, but it cannot promise precedence over another independent installation. Leaving a group or returning to standalone requires an explicit choice with that consequence explained.

## Privacy and security

The coordinator receives only authenticated random device identity, private group membership, a user-chosen display name, coarse recent-input age, participation/availability flags, and publishing-grant state. It does not need the Discord activity text, app/tool names, task/helper counts, T3 addresses, conversations, file paths, or T3 sign-in credentials. Device credentials belong in Keychain or Windows Credential Manager; redact codes and credentials from logs. Document service-side retention and the new activity-timing metadata before opt-in.

Pairing must rate-limit guesses, expire codes, prevent replay, and allow a lost computer to be removed. Removed devices cannot acquire or renew grants. A shared application ID is not an authentication secret. Local duplicate instances must share one device identity and have one publisher per signed-in OS session; launching a second copy is not another computer. Discord Stable/Canary and multiple signed-in OS sessions need explicit account/channel validation, not an assumption that the first pipe is the intended user.

## Release acceptance

Use at least two Macs and one Windows PC with the same Discord account and application ID, on separate networks as well as a shared network. Observe the profile from a second account. Automated clocks and fake coordination/Discord transports should verify the grant invariants; those tests do not replace the profile checks.

- Move from Windows to Mac A to Mac B while all three keep T3 open. Only the selected computer sends new activity; tool names/counts follow it without combining duplicate remote work.
- Type continuously without changing apps; the current computer remains selected. Workload updates and service heartbeats on an unattended computer never steal the selection.
- Use a browser or another unsupported app on the most recently used computer. Activity hides instead of reverting to an unattended machine.
- Test simultaneous input, near ties, explicit switching, manual selection, every computer idle, and choosing a computer where Discord is closed. Verify clear status and no oscillating publishes.
- Pause/off/lock/sleep/quit the selected computer during connect, T3 polling, a queued update, a renewal, and a handoff. Late callbacks cannot restore its publishing rights. Local opt-outs stay respected.
- Partition the selected computer from the coordinator while Discord still works; partition the challenger separately; restart the coordinator; lose its stored state. No overlapping live grants, offline fallback, or remembered owner may publish.
- Suspend a process beyond its grant, then resume it; delay replies beyond the client deadline; change the wall clock; deliver an old release or renewal out of order. No stale command or clear is issued after a new generation is in effect locally.
- Lose power on the old computer. Confirm bounded app-level takeover after grant expiry and measure actual Discord display convergence; record any stale-card window rather than asserting one cannot occur.
- Switch Discord accounts, run Stable and Canary together, switch OS users, and launch duplicate app copies. A different account/session must not silently publish under the linked selection.
- Revoke a device, replay a pairing code, exhaust pairing retries, and inspect logs/requests. No revoked grant renewal, cross-user linking, private activity content, or leaked credentials.

Until the coordinator and both native integrations pass these real-machine checks, describe linked-computer switching as a development feature, not a release-validated guarantee. Earlier builds and independent installations should enable sharing on only one computer.

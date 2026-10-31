# Native behavior specification

This specification is shared by the Swift and C# implementations. Neither platform hosts the other's UI or an always-running cross-platform sidecar.

## Baseline: app name

The user explicitly enables sharing and configures a public numeric Discord application ID. Until then no connection or activity update is made. A foreground observation contains an application enum (`t3`, `claude`, or `codex`) or no supported application. Detection uses application identity, not window titles, process command lines, conversation contents, or an AI inference.

Identity matching must be exact. Configurable mappings are necessary for application variants; a process containing `code` in its name is not evidence of Codex. Product identifiers must be verified on each release platform. The UI must distinguish an unsupported/unknown foreground application from a supported app with zero running tasks.

For a supported app, the only activity content in this version is a `details` string:

| App | Details |
| --- | --- |
| T3 Code | `Using T3 Code` |
| Claude Desktop | `Using Claude` |
| Codex Desktop | `Using Codex` |

App-name mode publishes no elapsed timer, project, model, count, party, buttons, or arbitrary source text. No manual free-text presence is needed. The connection must use the companion's own application identity, not impersonate a supported application. The optional [T3 activity extension](t3-activity.md) adds allowlisted tool names, measured task/helper counts, and explicitly enabled artwork while retaining the sharing gates below.

## Sharing gate

An activity is eligible only if all of these are true:

1. Persisted settings have finished loading.
2. Sharing is enabled, a valid numeric application ID is configured, and the user has not paused.
3. The session is unlocked and awake.
4. A current supported foreground application has been observed.

An unsupported foreground application clears presence. Opening the companion's settings may therefore clear it. No background-work claim is inferred from an app remaining open. After wake/unlock, refresh foreground identity before publishing again. A failed foreground read clears eligibility instead of retaining an old observation indefinitely.

Turning sharing off or pausing must invalidate pending work immediately. A delayed connect, throttled update, timer, or stale callback must never restore an earlier activity. Changing the application ID closes the old connection before using the new identity. Shutdown clears presence best-effort, closes the connection, and does not wait indefinitely.

## Multiple computers

Independent mode applies these gates separately on each computer and provides no cross-computer precedence. Linked mode additionally requires a current publishing grant from the private coordination service. It selects the computer with the most recent observed OS input, including when that computer's active app is unsupported. The required selection, safe handoff, failure behavior, and release checks are defined in [Sharing from multiple computers](multiple-computers.md). This is a development extension; real multi-computer Discord acceptance is pending.

## Discord transport

- Transport is local: UNIX domain socket on macOS, named pipe on Windows. Try IPC indices 0 through 9.
- Handshake is opcode 0 with protocol `v: 1` and public `client_id`.
- A frame is two unsigned 32-bit little-endian integers (opcode, UTF-8 payload byte length) followed by that payload. Emit one complete frame per write operation, accounting for short writes where the OS API requires it.
- Parse split headers, split payloads, and coalesced frames. Reject oversized frames (implementation cap 64 KiB) and invalid JSON. Do not allocate arbitrary lengths from the wire.
- Wait for READY before `SET_ACTIVITY`. Activity commands have a nonce and `args: {pid, activity}`. Clearing uses `activity: null`. Observe acknowledgments/errors rather than labeling a successful pipe write as a successful Discord update.
- Answer ping (opcode 3) with pong (opcode 4); handle close (opcode 2), EOF, connection errors, handshake timeout, and command timeout without terminating the application.
- Reconnect with bounded delay while sharing remains enabled. Restarting Discord must restore the latest eligible activity even when foreground state has not changed. A new connection resets duplicate suppression.
- Coalesce activity changes with a minimum five-second publish interval. This is a conservative product policy based on historical Discord guidance, not a claim about all current Discord limits. Privacy clearing takes priority over ordinary queued updates; drop the connection if clearing cannot be completed promptly.
- UI status differentiates disabled, paused, connecting/retrying, connected, and errors. An outgoing preview is not proof Discord accepted it.

## T3 activity and future adapters

The [T3 activity contract](t3-activity.md) defines the read-only connection, grouping, freshness, credentials, and plain-language output. Server metadata does not reveal the selected desktop tab. Investigate Codex Desktop observation separately from starting an app-server, and Claude Desktop separately from Claude Code hooks. App focus remains useful when richer metadata is unavailable.

Records should eventually carry source, environment, native session ID, optional proven underlying provider ID, optional parent ID, status, observation/expiry times, and explicitly supported metadata. Working/waiting counts include only fresh top-level observations; helpers are separate. Deduplicate only through proven identity, never project names. Unknown coverage is not zero. Hide project/model fields by default; use user aliases when sharing them. Future credentials belong in Keychain or Windows Credential Manager.

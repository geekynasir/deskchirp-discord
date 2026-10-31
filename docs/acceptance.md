# Native verification checklist

Record OS version, CPU architecture, Discord channel/version, and companion commit. These are release checks, not results already obtained on the Linux development host.

## macOS

- Build and run the test target and the packaged `.app` with supported Xcode tooling.
- Verify the menu-bar item, keyboard access, Settings, Quit, VoiceOver labels, dark appearance, and increased contrast.
- Confirm the actual bundle IDs for T3 Code, Claude Desktop, and Codex Desktop; test custom mappings.
- Verify no Accessibility or Screen Recording permission is requested merely to identify foreground apps.
- Test sleep/wake, user-session changes, screen lock/unlock, and quitting while a connection is pending.

## Windows

- Restore/build the WinUI project on a Windows machine with the Windows App SDK prerequisites; run core tests.
- Verify notification icon, context menu, close-to-tray, Show, Pause/Resume, Quit, and keyboard access.
- Check light/dark mode, high contrast, Narrator, scaling at 100%/150%/200%, and multiple monitors.
- Confirm exact foreground process identities for each supported app; test packaged-app variants and inaccessible processes.
- Test lock/unlock, suspend/resume, disconnecting a remote desktop session, Explorer restarting, and application shutdown.
- Verify packaging/runtime deployment on a clean machine without Visual Studio.

## Both platforms: real Discord client

- Fresh install: no Discord connection before opt-in. Invalid or empty public application ID cannot enable publishing.
- Focus each supported app and verify the actual profile displays only its approved `Using …` text.
- Focus an unsupported app, pause, disable, lock, sleep, or quit: activity clears and stays cleared.
- Rapidly change focus and settings while Discord is starting. No stale callback restores old or disabled activity.
- Launch Discord after the companion; restart Discord without changing focus; the current eligible presence returns.
- Stop/restart Discord repeatedly; no crash, busy reconnect loop, duplicate publishes, or permanently stuck connection.
- Simulate split/coalesced frames, large frames, malformed JSON, ping, close, handshake errors, command errors, and missing acknowledgments.
- Inspect outbound payloads: no window title, code, prompt, path, project, model, or fabricated counts.
- Distinguish outgoing preview from acknowledged Discord state. Revoke sharing and restart the companion: saved choices apply before any send.

## Both platforms: optional T3 activity

- Fresh install and an upgrade from an older build: **Show T3 activity** and **Show app icons** remain off. Existing Discord settings and app mappings survive the upgrade.
- Copy a T3 address from Settings → Environments → Copy MCP URL. Connect without administrator privileges, approve **Read only** in the browser, and confirm the app returns to **Connected**. Test both local HTTP and remote HTTPS environments.
- Cancel in the app, deny in the browser, let sign-in time out, and send an invalid callback. No listener remains open, and an existing saved connection stays usable. Confirm Disconnect removes the saved sign-in from Keychain or Windows Credential Manager.
- With sharing and T3 activity enabled, focus T3 Code. Run tasks and helpers with Claude Code, Codex, and Antigravity. Check that each helper counts under the tool that runs it, even when its parent uses another tool. A fork counts as a task.
- Confirm the busiest tool wins by running task/helper count. Ties retain the current tool; a new leader must stay ahead for 10 seconds. Waiting and starting work must be labeled as such, and finished work must disappear.
- Change counts without switching apps: the native preview and Discord update. Unknown tools show **Other tools**, with no private account or tool names.
- Test multiple result pages, repeated thread IDs, an incomplete or oversized response, network failure, and expired/revoked access. Failed or stale data falls back to **Using T3 Code**, never a partial or remembered total.
- Pause, lock, disconnect, change the T3 address, or switch to Claude/Codex while a poll or sign-in is pending. A late result must not restore activity or save a disconnected sign-in.
- Inspect requests using a synthetic test environment: only capabilities and thread-list tools are called. No conversation-reading tool or transcript access occurs. Inspect settings/logs for accidental credential storage.
- Enable icons after uploading the included artwork to the configured Discord application. Verify the T3 icon and supported tool badge on a real profile, including app-only mode. Disable icons and confirm both images disappear.
- Read the app at larger text sizes and with a screen reader. Helper text wraps, the full two-line Discord preview is available, and normal status messages contain no protocol names or raw errors.

Record which helpers T3 exposes as child threads. Helpers that exist only inside a provider's own run are not inferred or included in these counts.

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

Deep activity adapters need additional coverage for stale records, partial capabilities, top-level/helper counts, and proven duplicate session IDs before being enabled.

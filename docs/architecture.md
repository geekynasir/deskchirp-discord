# Architecture decision: two native desktop applications

Status: selected by the user on 2026-10-30. Supersedes the Electron companion proposal.

## Platform ownership

macOS owns its SwiftUI menu-bar interface, AppKit foreground observation, preferences, local Discord socket, lifecycle notifications, and eventually Keychain credentials. Windows owns its WinUI 3 settings window, native notification-area integration, foreground observation, preferences, local Discord named pipe, lifecycle notifications, and eventually Credential Manager credentials.

Use each platform's built-in text styles, spacing, colors, focus treatment, accessibility, light/dark mode, and reduced-motion behavior. The two interfaces need feature parity, not identical layouts. Standard native controls take precedence over recreating the old HTML concept.

## Shared behavior without a shared runtime

Swift and C# both have adequate native networking and JSON support. Implementing small platform services separately avoids an additional Rust/C++ build pipeline and FFI boundary before the activity sources are understood. Maintain a language-neutral specification and common fixtures to prevent behavior drift. Reconsider a shared core only if protocol/adapter complexity justifies its packaging and debugging cost.

Logical boundaries on each platform:

1. Foreground detector and future source adapters produce allowlisted observations.
2. A coordinator applies privacy choices, pause/suspend state, and source freshness.
3. A formatter produces the outgoing Discord activity.
4. A connection service owns framing, readiness, throttling, acknowledgment, reconnect, and clearing.
5. The native shell shows preferences, source capability, connection state, and the outgoing preview.

The current foundation is foreground-only. No OAuth, conversation fetching, remote T3 state polling, or task-count telemetry should be advertised as working yet.

## Native integrations

SwiftUI `MenuBarExtra` requires macOS 13+. AppKit `NSWorkspace` exposes the foreground application without screen recording or reading titles. Use native application identity and observe activation, sleep/wake, and session transitions. Some application identifiers vary by build, so explicit mappings and target-machine verification are necessary.

WinUI 3 is the desktop UI framework in Windows App SDK. The app is an unpackaged desktop process, allowing ordinary Win32 foreground and notification-area APIs alongside WinUI controls. WinUI does not supply a built-in tray component; the tray is an OS integration, not a second UI framework. The initial target is Windows 10 build 19041+; verify the selected Windows App SDK version's requirements before shipping.

Both apps connect to Discord running on the same computer. T3 may run remotely on the VPS; a later read-only adapter will connect to that environment over an authorized secure endpoint. The VPS itself cannot publish to Discord on the user's Mac or PC.

## Distribution and cost

Two native applications mean two build systems, release pipelines, accessibility reviews, and lifecycle implementations. Platform build/test jobs and shared behavior fixtures are necessary. Native binaries remove Chromium's baseline runtime, but no memory or battery targets have been measured yet.

macOS release requires a proper app bundle, signing and notarization. Windows release requires a choice of signed installer/MSIX or an unpackaged distribution with the Windows App SDK runtime deployed correctly. No signing material belongs in this repository or chat.

## Reference interfaces

- [Apple MenuBarExtra](https://developer.apple.com/documentation/swiftui/menubarextra)
- [Apple NSWorkspace](https://developer.apple.com/documentation/appkit/nsworkspace)
- [Microsoft WinUI 3](https://learn.microsoft.com/en-us/windows/apps/winui/winui3/)
- [Microsoft notification-area API](https://learn.microsoft.com/en-us/windows/win32/api/shellapi/nf-shellapi-shell_notifyiconw)
- [Microsoft foreground window API](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getforegroundwindow)
- [Discord Rich Presence](https://discord.com/developers/docs/rich-presence/overview)
- [Discord legacy IPC framing reference](https://github.com/discord/discord-rpc/blob/master/documentation/hard-mode.md)
- [T3 outside-agents guide](https://github.com/pingdotgg/t3code/blob/main/docs/user/outside-agents.md)

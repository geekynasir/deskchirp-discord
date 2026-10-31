# Developer Presence

An independent Discord Rich Presence companion for T3 Code, Claude Desktop, and Codex Desktop. Working name; unaffiliated with the applications it observes.

Two native applications:

- **macOS:** Swift, SwiftUI, and AppKit; menu-bar app with native settings, SF Symbols, and system appearance.
- **Windows:** C#, WinUI 3, and Windows App SDK; notification-area app with native Fluent controls and system appearance.

There is no Electron, embedded browser, or shared UI runtime. Platform implementations share a [behavior specification](spec/behavior.md), not rendered controls. See [architecture](docs/architecture.md) for the tradeoffs and adapter roadmap.

## Implementation scope

The initial source implements foreground-app presence: **“Using T3 Code,” “Using Claude,” or “Using Codex.”** Sharing is disabled by default. A user-supplied public Discord application ID and a locally running Discord desktop client are required. A public application ID is not a bot token or client secret.

Foreground detection does not establish agent activity or task counts. The authenticated T3 metadata adapter and richer Claude/Codex activity adapters remain future work. No conversation bodies, window titles, code, or file paths are intended for presence payloads.

This repository was created on Linux. Native compilation, application detection, actual Discord display, and desktop lifecycle behavior require macOS and Windows validation. The presence of CI definitions is not evidence of passing CI. Do not treat these sources as signed or distribution-ready releases.

The portable C# checks currently pass **77 tests** (75 core, 2 settings presentation). Swift tests and native application builds have not run. See [verification status](docs/validation.md).

## Development

Build each app on its target platform; see [macOS](macos/README.md) and [Windows](windows/README.md). Keep platform dependencies inside their own subdirectories. The [acceptance checklist](docs/acceptance.md) covers the real-client checks required before release.

## Relationship to T3 Code

This product is separate from the T3 Code fork. The upstream [scope request](https://github.com/pingdotgg/t3code/discussions/18348#discussioncomment-19293472) remains a separate track requiring maintainer approval before an upstream feature contribution. This companion does not need changes to T3 for its foreground-only capability.

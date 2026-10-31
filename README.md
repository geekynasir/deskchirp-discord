# Developer Presence

An independent Discord Rich Presence companion for T3 Code, Claude Desktop, and Codex Desktop. Working name; unaffiliated with the applications it observes.

Two native applications:

- **macOS:** Swift, SwiftUI, and AppKit; menu-bar app with native settings, SF Symbols, and system appearance.
- **Windows:** C#, WinUI 3, and Windows App SDK; notification-area app with native Fluent controls and system appearance.

There is no Electron, embedded browser, or shared UI runtime. Platform implementations share a [behavior specification](spec/behavior.md), not rendered controls. See [architecture](docs/architecture.md) for the tradeoffs and adapter roadmap.

## Implementation scope

The app shows **“Using T3 Code,” “Using Claude,” or “Using Codex.”** Sharing is disabled by default. A user-supplied public Discord application ID and a locally running Discord desktop client are required. A public application ID is not a bot token or client secret.

Optional **T3 activity** connects to one T3 environment with read-only access. It shows the busiest agent tool and separate task/helper counts, for example **“T3 Code · Claude Code”** and **“2 tasks · 4 helpers.”** A helper is an extra agent working on a task. [Connect T3 Code](docs/connect-t3.md) to use it. Artwork is a separate opt-in with an [included icon pack](assets/discord/README.md).

App detection alone does not establish agent activity. T3 counts come from fresh metadata, and unavailable data falls back to the app name. Claude Desktop and Codex Desktop remain app-name-only. Conversations, window titles, projects, model names, code, and file paths are not published.

Native development builds are available for Apple Silicon Macs, Intel Macs, and Windows x64 from the [successful GitHub Actions run](https://github.com/geekynasir/developer-presence/actions/runs/41190337628). Sign in to the repository account to download its artifacts; see [trying a build](docs/trying-builds.md). These are development bundles, not signed releases or installers.

CI passed **30 Swift tests on each Mac architecture** and **77 portable C# tests** (75 core, 2 settings presentation), built both macOS app bundles, and published the Windows app with its runtimes. Actual Discord display, application detection, and desktop lifecycle behavior still require validation on real machines. See [verification status](docs/validation.md).

## Development

Build each app on its target platform; see [macOS](macos/README.md) and [Windows](windows/README.md). Keep platform dependencies inside their own subdirectories. The [acceptance checklist](docs/acceptance.md) covers the real-client checks required before release.

## Relationship to T3 Code

This product is separate from the T3 Code fork. The upstream [scope request](https://github.com/pingdotgg/t3code/discussions/18348#discussioncomment-19293472) remains a separate track requiring maintainer approval before an upstream feature contribution. This companion does not need changes to T3 for its foreground-only capability.

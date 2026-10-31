# Deskchirp for Discord

Discord Rich Presence for AI coding tools, with native macOS and Windows apps. Show the app you’re using, share T3 Code agent activity, and follow your most recently used computer.

Created and maintained by [geekynasir](https://github.com/geekynasir). Independent and unaffiliated with Discord or the tools it observes.

Two native applications:

- **macOS:** Swift, SwiftUI, and AppKit; menu-bar app with native settings, SF Symbols, and system appearance.
- **Windows:** C#, WinUI 3, and Windows App SDK; notification-area app with native Fluent controls and system appearance.

There is no Electron, embedded browser, or shared UI runtime. Platform implementations share a [behavior specification](spec/behavior.md), not rendered controls. See [architecture](docs/architecture.md) for the tradeoffs and adapter roadmap.

## Implementation scope

The app shows **“Using T3 Code,” “Using Claude,” or “Using Codex.”** Sharing is disabled by default. A user-supplied public Discord application ID and a locally running Discord desktop client are required. A public application ID is not a bot token or client secret.

Optional **T3 activity** connects to one T3 environment with read-only access. It shows the busiest agent tool and separate task/helper counts, for example **“T3 Code · Claude Code”** and **“2 tasks · 4 helpers.”** A helper is an extra agent working on a task. [Connect T3 Code](docs/connect-t3.md) to use it. Artwork is a separate opt-in with an [included icon pack](assets/discord/README.md).

**Using several computers:** **Follow me** selects the linked computer you most recently used. It requires a [private linking service](coordination/README.md); no public service is deployed. [Set up your computers](docs/computers.md). Earlier builds and unlinked installations still act independently—enable sharing on only one of those computers. Real multi-computer Discord acceptance is pending.

App detection alone does not establish agent activity. T3 counts come from fresh metadata, and unavailable data falls back to the app name. Claude Desktop and Codex Desktop remain app-name-only. Conversations, window titles, projects, model names, code, and file paths are not published.

## Download

Get **[v0.1.0-preview.1 from GitHub Releases](https://github.com/geekynasir/deskchirp-discord/releases/tag/v0.1.0-preview.1)**.

| Computer | Download |
| --- | --- |
| Apple Silicon Mac | [macOS arm64](https://github.com/geekynasir/deskchirp-discord/releases/download/v0.1.0-preview.1/Deskchirp-0.1.0-preview.1-macos-arm64.zip) |
| Intel Mac | [macOS x86_64](https://github.com/geekynasir/deskchirp-discord/releases/download/v0.1.0-preview.1/Deskchirp-0.1.0-preview.1-macos-x86_64.zip) |
| Windows PC | [Windows x64](https://github.com/geekynasir/deskchirp-discord/releases/download/v0.1.0-preview.1/Deskchirp-0.1.0-preview.1-windows-x64.zip) |

Downloads include setup guides, license notices and build information. Verify them with the release’s `SHA256SUMS`. These are preview bundles, not signed installers. Read [setup](docs/trying-builds.md) before opening one. The optional [private linking service](coordination/README.md) has a separate source download on the same release page. The app needs Discord desktop and a public Discord application ID; no bot token or client secret.

CI covers **80 Swift tests on each Mac architecture**, **145 portable C# tests** (142 core, 3 settings presentation), **17 linking-service tests**, and a real HTTP handoff between two native client services. It also builds both Mac app bundles and the Windows app with its runtimes. Actual Discord display, cross-computer handoff, application detection, and desktop lifecycle behavior still require validation on real machines. See [verification status](docs/validation.md).

## Development

Build each app on its target platform; see [macOS](macos/README.md) and [Windows](windows/README.md). Keep platform dependencies inside their own subdirectories. The [acceptance checklist](docs/acceptance.md) covers the real-client checks required before release.

## Relationship to T3 Code

This product is separate from the T3 Code fork. The upstream [scope request](https://github.com/pingdotgg/t3code/discussions/18348#discussioncomment-19293472) remains a separate track requiring maintainer approval before an upstream feature contribution. This companion does not need changes to T3 for its foreground-only capability.

## Contributing and license

[Contributions](CONTRIBUTING.md), bug reports and focused feature suggestions are welcome. Read [privacy](docs/privacy.md) and [security reporting](SECURITY.md) before sharing logs or screenshots.

Original code is [MIT licensed](LICENSE), copyright 2026 geekynasir. [Third-party software and artwork notices](THIRD_PARTY_NOTICES.md) retain their respective terms and trademark rights.

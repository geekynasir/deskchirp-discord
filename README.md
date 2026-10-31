# Deskchirp for Discord

Discord Rich Presence for AI coding tools, with native macOS and Windows apps. Show the app you’re using, share T3 Code agent activity, and follow your most recently used computer.

Created and maintained by [geekynasir](https://github.com/geekynasir). Independent and unaffiliated with Discord or the tools it observes.

Two native applications:

- **macOS:** Swift, SwiftUI, and AppKit; menu-bar app with native settings, SF Symbols, and system appearance.
- **Windows:** C#, WinUI 3, and Windows App SDK; notification-area app with native Fluent controls and system appearance.

There is no Electron, embedded browser, or shared UI runtime. Platform implementations share a [behavior specification](spec/behavior.md), not rendered controls. See [architecture](docs/architecture.md) for the tradeoffs and adapter roadmap.

## Implementation scope

The app shows **“Using T3 Code,” “Using Claude,” or “Using Codex.”** Sharing is disabled by default. Open Discord desktop and turn on **Share on Discord**. Deskchirp includes its public Discord application ID and connects locally; users do not need a Developer Portal account, bot token, or client secret.

Choose **Allow T3 activity** to find the local T3 Code app and approve read-only access in your browser. Remote and custom addresses are under **Advanced**. Optional **T3 activity** connects to one approved environment. It shows the busiest agent tool and separate task/helper counts, for example **“T3 Code · Claude Code”** and **“2 tasks · 4 helpers.”** A helper is an extra agent working on a task. [Connect T3 Code](docs/connect-t3.md) to use it. App and agent artwork is included and enabled by default; no asset upload is needed. See the [icon pack](assets/discord/README.md).

**Using several computers:** **Follow me** selects the linked computer you most recently used. It requires a [private linking service](coordination/README.md); no public service is deployed. [Set up your computers](docs/computers.md). Earlier builds and unlinked installations still act independently—enable sharing on only one of those computers. Real multi-computer Discord acceptance is pending.

App detection alone does not establish agent activity. T3 counts come from fresh metadata, and unavailable data falls back to the app name. Claude Desktop and Codex Desktop remain app-name-only. Conversations, window titles, projects, model names, code, and file paths are not published.

## Download

Get **[v0.1.0-preview.3 from GitHub Releases](https://github.com/geekynasir/deskchirp-discord/releases/tag/v0.1.0-preview.3)**.

| Computer | Download |
| --- | --- |
| Apple Silicon Mac | [macOS arm64](https://github.com/geekynasir/deskchirp-discord/releases/download/v0.1.0-preview.3/Deskchirp-0.1.0-preview.3-macos-arm64.zip) |
| Intel Mac | [macOS x86_64](https://github.com/geekynasir/deskchirp-discord/releases/download/v0.1.0-preview.3/Deskchirp-0.1.0-preview.3-macos-x86_64.zip) |
| Windows PC | [Windows installer](https://github.com/geekynasir/deskchirp-discord/releases/download/v0.1.0-preview.3/Deskchirp-0.1.0-preview.3-windows-x64-setup.exe) |

Downloads include setup guides, license notices and build information. Verify them with the release’s `SHA256SUMS`. The Windows setup wizard installs the app and its runtimes for your account, adds a Start shortcut, and supports uninstalling from Installed Apps. Mac downloads are app bundles. These preview downloads are not publisher-signed. Read [setup](docs/trying-builds.md) before opening one. The optional [private linking service](coordination/README.md) has a separate source download on the same release page. The app needs Discord desktop; its public application ID is included.

T3 Code stays visible while minimized. Its unfinished work takes priority; otherwise the active supported developer app wins. If no supported app is active and T3 is still open, the card shows **Using T3 Code**. Closing T3 clears its task counts. Pause, screen lock, sleep and linked-computer grants still control sharing.

CI tests both Mac architectures, the portable C# core and settings, the linking service, and real HTTP handoff. It builds the native apps and exercises Windows installation, Start launch, version upgrade and uninstall. Real Discord display and clean-machine checks remain in [verification status](docs/validation.md).

## Development

Build each app on its target platform; see [macOS](macos/README.md) and [Windows](windows/README.md). Keep platform dependencies inside their own subdirectories. The [acceptance checklist](docs/acceptance.md) covers the real-client checks required before release.

## Relationship to T3 Code

This product is separate from the T3 Code fork. The upstream [scope request](https://github.com/pingdotgg/t3code/discussions/18348#discussioncomment-19293472) remains a separate track requiring maintainer approval before an upstream feature contribution. This companion does not need changes to T3 for app detection or T3 task counts.

## Contributing and license

[Contributions](CONTRIBUTING.md), bug reports and focused feature suggestions are welcome. Read [privacy](docs/privacy.md) and [security reporting](SECURITY.md) before sharing logs or screenshots.

Original code is [MIT licensed](LICENSE), copyright 2026 geekynasir. [Third-party software and artwork notices](THIRD_PARTY_NOTICES.md) retain their respective terms and trademark rights.

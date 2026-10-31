# Windows application

Native WinUI 3 settings and Win32 notification-area integration. No browser UI or Electron. Targets Windows 10 build 19041+ on x64 or ARM64. The first build job targets x64; ARM64 and native desktop behavior remain unverified.

## Build on Windows

Install Visual Studio 2022 with Windows application development tools, .NET 8 SDK, and Windows SDK. This unpackaged project uses the Windows App SDK 1.8 runtime; install the matching runtime for the architecture unless publishing self-contained.

From the repository root in a developer terminal:

```powershell
dotnet test windows/tests/DeveloperPresence.Tests/DeveloperPresence.Tests.csproj -c Release
dotnet build windows/src/DeveloperPresence.App/DeveloperPresence.App.csproj -c Release -p:Platform=x64 -p:RuntimeIdentifier=win-x64
```

Run the executable from its build output directory, retaining the accompanying runtime/dependency files. For an ARM64 build, use `Platform=arm64` and `RuntimeIdentifier=win-arm64`. A signed installer and clean-machine deployment are future work; a single copied EXE is not a complete release.

The portable C# core/tests can also run on Linux with .NET 8. That does not compile or validate WinUI XAML or call Windows APIs.

## First run

Sharing is off by default. Enter your own public application ID from the [Discord Developer Portal](https://discord.com/developers/applications), run Discord desktop on the same PC, then enable sharing. Do not enter a bot token or client secret. The app only reports focus using approved `Using …` strings.

The settings window closes to the notification area when a tray icon is available. The native tray menu provides Show, Pause/Resume, and Quit. If the tray icon cannot be created, closing exits instead of hiding an inaccessible process. Explorer restart recreates the icon.

Preferences are stored at `%LOCALAPPDATA%\DeveloperPresence\settings.json`. Executable mappings are exact and case-insensitive. T3 defaults include `T3 Code (Beta).exe` and `T3 Code (Nightly).exe`, derived from upstream build configuration. Desktop app variants may need a custom mapping; verify the executable on your PC. Matching a process does not verify its publisher or imply an agent is working. CLI commands should not be mapped as desktop identities by default.

Lock, suspend, or remote-session disconnect clears sharing. An unlock refreshes foreground state. The initial remote-session behavior is conservative: if reconnect does not emit unlock, sharing can remain cleared until the next unlock or application restart. Validate this against your remote-desktop setup.

See the [acceptance checklist](../docs/acceptance.md). Native build/run, real Discord, scaling/accessibility, power/session messages, packaging, and signing have not been validated on this Linux VPS.

# Trying a development build

These are unsigned development builds, not signed releases or installers. CI compilation and automated tests do not verify real Discord display, desktop focus, lock/sleep behavior, or clean-machine installation. Sharing is off by default. macOS lock-state handling still needs native validation; pause or quit the companion before leaving it unattended.

Download the artifact from the [**Native apps** GitHub Actions run](https://github.com/geekynasir/developer-presence/actions/runs/41239907490) while signed in to the repository account. Artifacts expire after 14 days.

## macOS 13 or newer

Choose `macos-arm64` for Apple Silicon (M-series), or `macos-x86_64` for Intel. Extract the artifact and its inner app ZIP. Move `DeveloperPresence.app` to Applications, then open it. Look for the menu-bar item; the app has no Dock icon.

The app has an ad-hoc development signature, not an Apple Developer ID signature or notarization. Gatekeeper can block downloaded development apps. If you trust this build, use macOS System Settings > Privacy & Security > Open Anyway after attempting to open it. Do not disable Gatekeeper globally.

## Windows 10 build 19041 or newer / Windows 11 (x64)

Extract the entire `windows-x64` artifact into a folder, then run `DeveloperPresence.App.exe` from that folder. Keep every DLL and subdirectory beside the EXE. The artifact includes .NET and Windows App SDK runtimes, but is not a single-file executable or an installer. A clean PC may also require the Microsoft Visual C++ v14 x64 Redistributable. Windows may warn because the app is unsigned. Native installation and runtime behavior remain unverified until tested on a real PC.

The app lives in the notification area. Closing its settings hides the window when the tray icon is available. Use the tray menu to quit.

## Discord setup on either platform

1. Run the Discord desktop client locally on the same computer.
2. Create an application at https://discord.com/developers/applications and copy its public **Application ID**. Do not enter a bot token or a client secret.
3. Open the companion's settings, paste the Application ID, and enable sharing.
4. Focus a supported desktop application and check your Discord activity. Discord's activity-sharing setting must permit it.

By default, sharing shows only `Using T3 Code`, `Using Claude`, or `Using Codex`. Builds with the optional **Show T3 activity** setting can also show agent tool names and task/helper counts after [connecting T3 Code](connect-t3.md). Use the included `CONNECT-T3.md` when trying a downloaded build. Older builds without that setting remain app-name-only. Projects, models, and conversation contents are not published. Codex on macOS may need an entry under App Detection; desktop app variants may also need one.

Pause, disable, or quit should clear presence. Verify these behaviors, Discord restarts, screen lock, and sleep before relying on the app. See `docs/acceptance.md` in the source repository for the full checklist.

## Using several computers

The new builds include **Computers → Follow me**. It selects the linked computer you most recently used, with a short wait while the previous computer stops sharing. A [private linking service](../coordination/README.md) is required; none is deployed for you. Follow [computer setup](computers.md), also included as `COMPUTERS.md` in the download. Earlier builds and unlinked installations cannot agree on which computer should win.

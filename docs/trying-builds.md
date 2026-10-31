# Set up Deskchirp for Discord

These are unsigned development builds, not signed releases or installers. CI compilation and automated tests do not verify real Discord display, desktop focus, lock/sleep behavior, or clean-machine installation. Sharing is off by default. macOS lock-state handling still needs native validation; pause or quit the companion before leaving it unattended.

Download your platform’s ZIP from [GitHub Releases](https://github.com/geekynasir/deskchirp-discord/releases). No GitHub sign-in is needed for public release downloads. Choose the latest preview, and use its `SHA256SUMS` to verify the files. GitHub’s automatic “Source code” downloads contain source, not ready-to-run apps.

## macOS 13 or newer

Choose the download ending in `macos-arm64.zip` for Apple Silicon (M-series), or `macos-x86_64.zip` for Intel. Extract the ZIP. Move `Deskchirp.app` to Applications, then open it. Look for the menu-bar item; the app has no Dock icon.

The app has an ad-hoc development signature, not an Apple Developer ID signature or notarization. Gatekeeper can block downloaded development apps. If you trust this build, use macOS System Settings > Privacy & Security > Open Anyway after attempting to open it. Do not disable Gatekeeper globally.

## Windows 10 build 19041 or newer / Windows 11 (x64)

Extract the entire `windows-x64.zip` download into a folder, then run `Deskchirp.exe` from that folder. Keep every DLL and subdirectory beside the EXE. The artifact includes .NET and Windows App SDK runtimes, but is not a single-file executable or an installer. A clean PC may also require the Microsoft Visual C++ v14 x64 Redistributable. Windows may warn because the app is unsigned. Native installation and runtime behavior remain unverified until tested on a real PC.

The app lives in the notification area. Closing its settings hides the window when the tray icon is available. Use the tray menu to quit.

## Discord setup on either platform

1. Run the Discord desktop client locally on the same computer.
2. Open Deskchirp settings and turn on **Share on Discord**. It connects automatically using Deskchirp’s included public application ID.
3. Focus a supported desktop application and check your Discord activity. Discord’s activity-sharing setting must permit it.
4. For task and helper counts, choose **Allow T3 activity** and approve read-only access in your browser. The local T3 address is found automatically. Remote addresses and custom Discord application IDs are under **Advanced**.

By default, sharing shows only `Using T3 Code`, `Using Claude`, or `Using Codex`. Builds with the optional **Show T3 activity** setting can also show agent tool names and task/helper counts after [connecting T3 Code](connect-t3.md). Use the included `CONNECT-T3.md` when trying a downloaded build. Older builds without that setting remain app-name-only. Projects, models, and conversation contents are not published. Codex on macOS may need an entry under App Detection; desktop app variants may also need one.

Pause, disable, or quit should clear presence. Verify these behaviors, Discord restarts, screen lock, and sleep before relying on the app. See `docs/acceptance.md` in the source repository for the full checklist.

## Using several computers

The new builds include **Computers → Follow me**. It selects the linked computer you most recently used, with a short wait while the previous computer stops sharing. A [private linking service](../coordination/README.md) is required; none is deployed for you. Follow [computer setup](computers.md), also included as `COMPUTERS.md` in the download. Earlier builds and unlinked installations cannot agree on which computer should win.

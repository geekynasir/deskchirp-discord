# Verification status

Updated on 2026-10-31. The Ubuntu ARM64 VPS controls GitHub-hosted native builds; source, downloaded artifacts, SDK, package caches, and scratch on the VPS remain on the mounted `/data` volume.

Successful native CI: [run 41190337628](https://github.com/geekynasir/developer-presence/actions/runs/41190337628), source commit `69a4d6572dce4f4cf198ca0c13e67cd61eedafbf`. The companion repository is private in `geekynasir`'s account.

## Executed successfully

- Portable C# core suite: **75 passed, 0 failed** with .NET SDK 8.0.427.
- Windows settings-presentation suite: **2 passed, 0 failed**. This also compiles the platform's view-model and Win32 helper sources without invoking native Windows APIs.
- The same **77 C# tests passed in CI** on an Ubuntu runner.
- **30 Swift tests passed on each of Apple Silicon and Intel macOS runners**, including scripted Discord protocol tests, shared fixtures, formatter/settings checks, and coordinator checks.
- Release-mode macOS `.app` bundles compiled for arm64 and x86_64; plist validation and ad-hoc codesign verification passed. Both bundles are archived as downloadable artifacts.
- Windows x64 WinUI/XAML compilation and self-contained publication succeeded on `windows-2022`; the artifact includes .NET and Windows App SDK runtimes. The SDK BuildTools dependency now matches the Windows App SDK's minimum requirement.
- All three artifacts were downloaded to `/data` and passed archive CRC checks. Both Mac binaries have the expected Mach-O CPU architecture; the Windows executable is an x64 PE, with the app's XAML resources, .NET runtime, and WinUI runtime present in its 507-file folder. Local SHA-256 checksums are stored alongside the downloaded archives.
- Project XML and XAML XML parsing, app plist inspection, shared Swift fixture-copy equality, source whitespace checks, and macOS packaging-script shell syntax.

Protocol tests use an in-memory duplex transport. They exercise READY gating, command nonces, acknowledgment/error handling, malformed input, frame bounds, ping/pong, EOF, reconnect, and cancellation. Shared fixtures cover the static presence payloads. These are useful source checks, not evidence of a real Discord profile displaying the activity.

The first native builds caught a Swift overload ambiguity, an outdated Windows SDK dependency, and test replies racing the connection handshake. These were corrected before the successful run. The Swift package uses Swift 5 language mode; concurrency warnings remain for captured test state and the serialized clear completion flag, so Swift 6 strict concurrency migration remains separate work.

## Not yet verified

- Launching the packaged macOS and Windows apps on real user machines.
- Real foreground detection, Discord display, lock/sleep, menu/tray behavior, accessibility, and clean-machine installation on either platform.
- Release signing, notarization and installer distribution.

The Linux environment has no Swift/Xcode or Windows desktop tooling; the native builds ran remotely on the appropriate GitHub runners. Downloaded artifacts are development builds, not signed or distribution-ready releases. Follow [acceptance.md](acceptance.md) on target machines before a release. In particular, validate initial Mac lock state and the distributed lock notification behavior described in the [macOS README](../macos/README.md).

## Product scope

The source provides a native foreground-presence foundation. T3 read-only metadata/OAuth pairing, working/waiting counts, verified session deduplication, richer Claude/Codex telemetry, artwork and playful templates are not implemented. The companion does not modify T3 Code and has no upstream PR.

# Verification status

Recorded on 2026-10-30 on the Ubuntu ARM64 VPS. Source, SDK, package caches, scratch and build outputs are on the mounted `/data` volume.

## Executed successfully

- Portable C# core suite: **75 passed, 0 failed** with .NET SDK 8.0.427.
- Windows settings-presentation suite: **2 passed, 0 failed**. This also compiles the platform's view-model and Win32 helper sources without invoking native Windows APIs.
- Project XML and XAML XML parsing, app plist inspection, shared Swift fixture-copy equality, source whitespace checks, and macOS packaging-script shell syntax.

Protocol tests use an in-memory duplex transport. They exercise READY gating, command nonces, acknowledgment/error handling, malformed input, frame bounds, ping/pong, EOF, reconnect, and cancellation. Shared fixtures cover the static presence payloads. These are useful source checks, not evidence of a real Discord profile displaying the activity.

## Prepared but not executed

- Swift compilation, XCTest suite, app bundle build and launch.
- WinUI application/XAML compilation and launch.
- GitHub Actions jobs in `.github/workflows/native.yml`; this local repository has not been published or run in CI.
- Real foreground detection, Discord display, lock/sleep, menu/tray behavior, accessibility, and clean-machine installation on either platform.
- Release signing, notarization and installer distribution.

The Linux environment has no Swift/Xcode or Windows desktop tooling. Follow [acceptance.md](acceptance.md) on target machines before a release. In particular, validate initial Mac lock state and the distributed lock notification behavior described in the [macOS README](../macos/README.md).

## Product scope

The source provides a native foreground-presence foundation. T3 read-only metadata/OAuth pairing, working/waiting counts, verified session deduplication, richer Claude/Codex telemetry, artwork and playful templates are not implemented. The companion does not modify T3 Code and has no upstream PR.

# macOS application

Native SwiftUI menu-bar app with AppKit foreground detection and native settings. Targets macOS 13+ with Swift 5.9+ / Xcode 15+ tooling. No Electron or embedded browser.

## Build on a Mac

From the repository root:

```bash
swift test --package-path macos
bash macos/scripts/build-app.sh
open macos/dist/Deskchirp.app
```

The packaging script builds a release executable, creates a dockless `.app` bundle (`LSUIElement`), and applies an ad-hoc signature. Developer ID signing, notarization, release artwork, and an installer are not implemented. Ad-hoc signing is for local development.

GitHub-hosted macOS runners have passed all 71 Swift tests on Apple Silicon and Intel and built, ad-hoc signed, and archived both app bundles. Download the matching artifact from the [successful build](https://github.com/geekynasir/deskchirp-discord/releases/tag/v0.1.0-preview.1) to try it without Xcode; see [download instructions](../docs/trying-builds.md). App launch, actual Discord, and desktop lifecycle behavior still need real-machine validation. Follow the [target-platform acceptance checklist](../docs/acceptance.md) before distributing the app.

## First run

Open Settings from the menu-bar item. Start Discord desktop locally and turn on **Share on Discord**. Deskchirp’s public application ID is included; no Developer Portal setup is needed. No bot token, client secret, or AI calls are required.

Sharing is off by default. Basic sharing shows `Using T3 Code`, `Using Claude`, or `Using Codex`. Optionally [connect T3 Code](../docs/connect-t3.md) and enable **Show T3 activity** to show its busiest agent tool with task and helper counts. Conversations, project names, models, and file paths are never published.

Default bundle identities are `com.t3tools.t3code` and `com.anthropic.claudefordesktop`. Codex Desktop uses a user-configured mapping until its bundle identity is verified on the target machine. Development builds can also need their own mapping. Settings show the locally detected bundle identifier to help configure it; that identifier is not included in the Discord payload.

Preferences use UserDefaults in the app's bundle domain. No credential is needed for foreground-only presence. Optional T3 sign-in is saved in Keychain. Disconnect removes the saved sign-in; revoke access in T3 Code as well if needed.

## Lifecycle verification

Sleep, display sleep, inactive-session, and screen-lock gates are tracked independently: waking a display must not undo an outstanding lock. Workspace lifecycle events are public APIs. The distributed `com.apple.screenIsLocked` / `com.apple.screenIsUnlocked` notification names are not public SDK constants, and initial lock-state detection is not established by receiving future events. Verify lock handling and startup behavior on every supported macOS release before claiming reliable lock privacy.

The local Discord client owns readiness, nonces, framing, retries, and throttling on a serial background queue. Connection clearing is best-effort followed by socket closure. Actual Discord display, menu behavior, accessibility, sleep/lock, and quitting during a pending connection need Mac validation.

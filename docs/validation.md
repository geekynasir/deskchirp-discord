# Verification status

Updated 2026-10-31. T3 activity is implemented in both native companions. The tested application source is `1c4e4c1591972c64bbc20ed8915b617f147733ef`; later documentation changes do not change those binaries.

Native builds: [run 41198285124](https://github.com/geekynasir/developer-presence/actions/runs/41198285124). The repository and artifacts are private; sign in with an account that has access. Artifacts are kept for 14 days.

## Automated checks

- **71 Swift tests on each Mac architecture**, Apple Silicon and Intel. These cover the existing Discord protocol/privacy checks, settings migration, task/helper grouping, tool selection, TTL fallback, MCP sessions, bounded streaming responses, browser sign-in, and real local callback requests (including rejection of a missing issuer).
- **122 C# core tests and 3 Windows settings tests**, locally and on the portable CI runner. These include shared activity cases, session/stream handling, late sign-in after Disconnect, late polls after invalidation, endpoint changes, and slow polling without blocking active-app detection.
- Release macOS app bundles, plist checks, and ad-hoc signature verification for arm64 and x86_64.
- Windows x64 WinUI/XAML compilation and self-contained publication, including .NET and Windows App SDK runtimes.
- JSON, project/XAML XML, plist, shared fixture equality, packaging shell syntax, and source whitespace checks.

The shared synthetic fixtures contain mixed agent tools, helpers that use a different tool from their parent, forks, repeated IDs, waiting/completed work, and private-name bait. Unknown driver labels never become public names. Transport tests use controlled responses, not a user's live conversations. The actual Mac callback tests run only against ephemeral local test listeners.

Swift 5 language mode is used. Concurrency warnings remain, including lock calls from asynchronous functions and captured test variables; Swift 6 strict-concurrency migration is separate work. Passing these tests does not establish that every race or platform behavior is covered.

## Downloaded builds and visual checks

The previous successful build of the same activity implementation, [run 41198069787](https://github.com/geekynasir/developer-presence/actions/runs/41198069787), passed all four jobs. Native artifacts are downloaded under `/data/ubuntu/tmp/developer-presence/`; the final run includes stable setup links and explicit appearance selection for Mac screenshot capture.

The Mac settings window was launched and its light and dark screenshots inspected: native controls, wrapping helper text, clear setup and privacy choices, and the off-by-default state render correctly in the captured view. A Windows light-mode settings screenshot was also inspected; the app launches and its sharing controls and Discord preview render correctly. Longer settings remain scrollable. This does not cover every state, tab, scaling setting, or screen reader.

Both downloaded Mac archives passed CRC checks and contain the expected Mach-O CPU architecture. The Windows folder contains an x64 PE executable, XAML resources, .NET/WinUI runtimes, setup notes, and artwork. Local checksums accompany the downloads. Hosted-runner launch captures do not replace clean-machine installation checks.

## Checks still needed on real machines

- T3 browser approval against a real local/remote environment, secure-store persistence, expiry, and revocation.
- A real Discord profile displaying the card and uploaded artwork, including reconnect after Discord restarts.
- Exact app identities, active-app transitions, menu/tray behavior, lock/sleep, and quitting during pending operations.
- Screen readers, high contrast, larger text/scaling, multiple monitors, and clean-machine installation.
- Release signing, Mac notarization, and installers.

The Linux VPS has no local Xcode or Windows desktop. Native builds and screenshots run on the matching GitHub hosts. These are development builds, not distribution-ready releases. Follow [acceptance.md](acceptance.md) before release.

## Scope

The optional T3 connection reads only capabilities and thread lists from one explicitly connected environment. It shows the busiest public agent tool and separate task/helper counts, with waiting/starting work labeled. Failed, incomplete, expired, or unavailable activity falls back to `Using T3 Code`. Only helpers that T3 exposes as child threads are counted; internal provider helpers are not inferred.

Claude Desktop and Codex Desktop remain app-name-only. Conversations, projects, thread titles, model names, private provider labels, and file paths are excluded from the Discord card. Tokens and cost are outside this feature. The companion does not modify upstream T3 Code. See [the setup guide](connect-t3.md) and [the companion PR](https://github.com/geekynasir/developer-presence/pull/1).

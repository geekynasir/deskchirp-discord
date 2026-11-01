# Verification status

Updated 2026-11-01. Preview 4 replaces the Windows setup wizard with a one-click installer and in-app updates, and adds card styles, remote T3 environments and opt-in token usage. Preview 3 added bundled artwork and minimized T3 activity. Build results and native screenshots are recorded in the [Native apps history](https://github.com/geekynasir/deskchirp-discord/actions/workflows/native.yml). This page distinguishes automated checks from real-device acceptance. Each release download carries source/build metadata and license notices; `SHA256SUMS` verifies published downloads. No linking service has been deployed.

[Installer run 41249298736](https://github.com/geekynasir/deskchirp-discord/actions/runs/41249298736) passed both native Mac architectures, Windows publication, portable tests and coordination checks. Its Windows smoke check installed an older-version setup fixture, launched from the Start shortcut, upgraded to the current version and uninstalled while preserving user data. The fixture used the current app payload, so this does not establish compatibility with all historical versions.

## Automated checks

- **Swift tests on each Mac architecture**, Apple Silicon and Intel. These cover the existing Discord protocol/privacy checks, settings migration, task/helper grouping, tool selection, TTL fallback, MCP sessions, bounded streaming responses, browser sign-in, and real local callback requests (including rejection of a missing issuer).
- **160 C# core tests and 5 Windows settings tests**, locally and on the portable CI runner. These include shared activity cases, session/stream handling, late sign-in after Disconnect, late polls after invalidation, endpoint changes, and slow polling without blocking active-app detection.
- Linked-mode native tests cover account fingerprints, standby handshakes without activity, publishing gates, idle time, expiry, delayed grants, credential recovery, unlinking, and a selected computer with Discord closed.
- **17 Python service tests** cover enrollment, hashed device tokens, account isolation, concurrent claims, near ties, unchanged input, idle/unavailable devices, Discord-closed selection, grant expiry, revocation, restart quarantine, and real HTTP requests.
- An additional integration check runs **two actual C# client services against the Python server over TCP**, with simulated OS input and real grant expiry. Enrollment, an idle peer staying quiet, the old client stopping, and exclusive takeover passed locally and in CI.
- Release macOS app bundles, plist checks, and ad-hoc signature verification for arm64 and x86_64.
- Windows x64 WinUI/XAML compilation and self-contained publication, including .NET and Windows App SDK runtimes.
- Minimized T3 count selection, active-app fallback, quitting T3, pause/disable clearing, artwork URL allowlisting and persisted artwork opt-out.
- JSON, project/XAML XML, plist, shared fixture equality, packaging shell syntax, and source whitespace checks.

The shared synthetic fixtures contain mixed agent tools, helpers that use a different tool from their parent, forks, repeated IDs, waiting/completed work, and private-name bait. Unknown driver labels never become public names. Transport tests use controlled responses, not a user's live conversations. The actual Mac callback tests run only against ephemeral local test listeners.

Swift 5 language mode is used. Concurrency warnings remain, including lock calls from asynchronous functions and captured test variables; Swift 6 strict-concurrency migration is separate work. Passing these tests does not establish that every race or platform behavior is covered.

## Downloaded builds and visual checks

Artifacts from the pre-release linked-computer build were downloaded and inspected locally. Both Mac archives passed CRC checks and contain the expected Mach-O CPU architecture. The Windows folder contains an x64 PE executable, XAML resources, .NET/WinUI runtimes, setup notes, and artwork. Local checksums accompany the downloads.

The captured Mac General settings window was inspected in light and dark appearance, plus the new Computers tab in light appearance. Native controls, linking fields, explanatory text, and the off-by-default state render correctly in the captured views. A Windows light-mode settings screenshot was also inspected; the app launches and its sharing controls, Discord preview, and collapsed Computers section render correctly. The expanded Windows linking form, other connection states, scaling, and screen readers still need hands-on checks.

Hosted-runner launch captures and artifact inspection do not replace clean-machine installation or real-device acceptance.

## Checks still needed on real machines

- Linked computers: at least two Macs and one Windows PC, the same Discord account/application ID, and a second account observing the profile. Check switching, near ties, unsupported apps, Discord closed, five-minute idle hiding, local pause, lock/sleep, crashes, and delayed clears.
- Network partitions, a service restart, process suspension beyond a grant deadline, remote desktop/input sharing, Discord Stable/Canary, and multiple OS-user sessions. Automated grant checks do not prove Discord’s global display ordering.
- T3 browser approval against a real local/remote environment, secure-store persistence, expiry, and revocation.
- A real Discord profile displaying the card and external artwork, including reconnect after Discord restarts.
- Exact app identities, active-app transitions, menu/tray behavior, lock/sleep, and quitting during pending operations.
- Screen readers, high contrast, larger text/scaling, multiple monitors, and clean-machine installation.
- Release signing, Mac notarization, and installer use on a clean personal Windows machine.

The Linux VPS has no local Xcode or Windows desktop. Native builds and screenshots run on the matching GitHub hosts. These are development builds, unsigned previews with the real-device checks below still pending. Follow [acceptance.md](acceptance.md) before release.

## Scope

The optional T3 connection reads only capabilities and thread lists from one explicitly connected environment. It shows the busiest public agent tool and separate task/helper counts, with waiting/starting work labeled. Failed, incomplete, expired, or unavailable activity falls back to `Using T3 Code`. Only helpers that T3 exposes as child threads are counted; internal provider helpers are not inferred.

Follow me requires explicit computer enrollment and a private HTTPS linking service. It selects recent OS input, never workload size or background polls. Only the selected computer’s T3 environment contributes counts; remote work is never added across computers. Missing service access stops sharing instead of reverting to independent publishing. Discord may still display an already accepted activity temporarily, so instantaneous handoff and absolute global exclusivity are not claimed. The linking service receives computer names, availability, coarse input timing, and a group-specific account fingerprint, but no activity text or T3 information. See [computer setup](computers.md) and [the real-device checks](../spec/multiple-computers.md#release-acceptance).

Claude Desktop and Codex Desktop remain app-name-only. Conversations, projects, thread titles, model names, private provider labels, and file paths are excluded from the Discord card. Tokens and cost are outside this feature. The companion does not modify upstream T3 Code. See [the setup guide](connect-t3.md) and [the companion PR](https://github.com/geekynasir/deskchirp-discord/pull/1).

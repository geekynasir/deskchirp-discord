# Recommended Rich Presence feature set

Reviewed 2026-10-31. This is a design and adapter roadmap. Current native development builds still publish foreground identity only; these features have not been added to those binaries.

## Product direction

Keep the independent SwiftUI/WinUI companion. Ship a coherent visual experience across T3 Code, Claude Desktop, and Codex Desktop, while letting each adapter declare which deeper fields it actually supports. A shared appearance does not require inventing equal telemetry access.

Continue the existing [T3 scope discussion](https://github.com/pingdotgg/t3code/discussions/18348#discussioncomment-19293472) if maintainers respond. A small metadata/usage integration proposal is more reusable for this multi-app product than putting its entire Discord subsystem in T3. Do not open another unapproved feature PR. The [contribution guide](https://github.com/pingdotgg/t3code/blob/main/CONTRIBUTING.md#prior-approval) requires explicit scope approval; a discussion link or bot review alone does not satisfy it. Its model-authorship policy also applies to any future upstream code contribution.

## Curated priorities

| Priority | Feature | Data prerequisite |
| --- | --- | --- |
| First | Large app artwork for T3, Claude, Codex | Curated reusable artwork and the companion's own Discord application/assets |
| First | One small overlay: app/provider or activity | Static provider choice or confirmed adapter status; never guess from focus |
| First | App-only, workload, and usage presets | Enable only presets supported by the selected adapter |
| First | Optional elapsed time, complete outgoing preview, pause/stealth | A clearly defined clock scope; privacy settings loaded before any publication |
| First | Project aliases, independent field toggles, exclusions | Manual selection initially; metadata adapter for automatic project identity |
| Next | Working tasks and attention counts | T3 thread metadata; validate attention semantics and pagination |
| Next | Optional model/provider and project label | Authorized metadata and explicit pinned selection; no active-tab inference |
| Next | Optional public-repository/about buttons | User-approved public URL; maximum two buttons |
| Investigate | Provider usage today with token-category and precision options | Existing T3 aggregate RPC, validated auth, version, source coverage and freshness |
| Investigate | API-equivalent estimated cost, cache breakdown | Matching price provenance and usage coverage |
| Later | Exact pinned-thread/session usage, context percentage | Narrow adapter exposing the correct counters without conversation bodies |
| Later | Helpers, milestones, background-work mode | Verified lifecycle, session identity, deduplication and explicit disclosure settings |

Suggested playful copy includes `Herding 3 agents`, `1 needs you`, and `Crunched 1.2M tokens`. Milestones use measured counts. Let users choose wording, token category, rounding, and supported scope; never let a setting manufacture a token total. Avoid gamifying estimated expenditure as a score.

Offer two explicit selection modes: **Follow active app** and **Keep background work visible**. The latter needs trustworthy workload adapters and a user-selected app/project priority. Aggregate top-level tasks separately from helpers, and deduplicate the same sessions seen through multiple adapters. Do not reset the elapsed clock merely because an agent moves from thinking to waiting.

## What mature VS Code extensions teach us

[Discord Presence](https://github.com/iCrawl/discord-vscode) and [VSCord](https://github.com/leonardssh/vscord) show language artwork, editor badges, editing/debugging/idle states, workspace/file labels, configurable templates, elapsed time, and Git/repository links. VSCord adds detailed exclusion rules, privacy mode, image overrides, and two configurable buttons. Their configuration schemas are the primary evidence; see [the comparison](vscode-presence.md).

Transfer their artwork, text hierarchy, preview, aliases, exclusions, clocks, and reconnect behavior. Cursor positions and diagnostics come from editor APIs; our foreground detector cannot supply them. Replace those editor details with confirmed task/workload information when an adapter is present.

## What the community T3 PR actually shows

[PR #18346](https://github.com/pingdotgg/t3code/pull/18346) exposes four optional fields: project, model, agent activity, elapsed time. Its [sample screenshot](https://github.com/snekxs/t3code/releases/download/discord-presence-evidence-20261011/project-model-activity.png) shows a T3 logo, `T3 Code`, `In OpenMouse Project`, `Claude Opus 5.5 · Idle`, and an elapsed timer. It counts top-level work across connected environments and excludes delegated subagents. It implements **no token or cost display**.

The GitHub API reports the PR OPEN and unmerged, despite a maintainer comment saying it would be closed pending approval. Scope approval in Discussion #18348 remains unestablished. Its internal renderer can identify the viewed thread; an external companion cannot infer that from the server's thread list. Use a user-pinned thread/project instead.

## Usage is feasible, with narrower claims

T3's source already contains [`server.getUsageSummary`](https://github.com/pingdotgg/t3code/blob/main/packages/contracts/src/rpc.ts), with a transcript-free [`UsageSummary`](https://github.com/pingdotgg/t3code/blob/main/packages/contracts/src/usage.ts). It is guarded by `diagnostics:read`, returns provider/model/day or hour aggregates, and includes cost provenance and source/pricing health. This corrects the initial delegated claim that every usage endpoint returns full conversations.

This is an application RPC, not a documented public MCP tool. It scans configured provider transcript directories and can include usage outside T3. It does not identify a selected desktop thread. The honest first label is **Provider usage today**, with the selected provider/source made clear. Exact session/project totals and foreground-app attribution need further adapter work. An upstream wrapper might expose the existing aggregates rather than build new accounting.

For Claude Desktop and Codex Desktop, focus detection is established; deeper GUI-session telemetry is not. Codex's documented app-server includes token notifications and local transports, but attachment to the running desktop must be demonstrated separately. Claude Code's documented status line offers context and estimated cost, but it is a distinct integration from Claude Desktop. See [desktop feasibility](desktop-usage.md).

## Accounting and privacy contract

- Separate cumulative processed tokens, current context occupancy, and subscription limits.
- Input totals include uncached input, cache reads and cache writes once. Reasoning is a subset of output; do not add it twice.
- Do not sum repeated cumulative snapshots. Deduplicate source/session identities across remote environments and restarts.
- Label model-priced costs `API est. ~$1.84`. Provider-reported costs are provenance, not independent evidence of an actual bill. Subscription use cannot be translated directly into incremental expenditure.
- Unknown/partial/stale data stays omitted or explicitly qualified. It is never silently shown as zero or as a complete total.
- Token, cost, project, model, and background-work disclosure are independently opt-in. Apply exclusions before aggregation; disable aggregate sources that cannot honor those exclusions.
- Keep prompts, titles, code, paths, source fingerprints, credentials, and private URLs out of every visible line, tooltip, image and button.

Discord offers two primary text lines, one large image, one small overlay, hover text, a clock, and at most two buttons. Use a small uploaded asset pack on a companion-owned public application ID. Keep the registered companion title until dynamic name overrides are validated for our transport. More detail belongs in native settings, not an overloaded profile card. See [official Discord findings](discord-display.md).

## Recommended next implementation

Implement artwork, presets, privacy controls, and the T3 workload adapter first. Then evaluate the existing aggregate RPC for optional provider/day usage. Treat exact thread tokens, desktop attachment, and cost provenance as separate adapter milestones with real-machine validation before enabling them.

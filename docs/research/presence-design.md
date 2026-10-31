# Presence card design

Researched 2026-10-31. This is a design proposal for the independent companion. It is **not** a description of shipped behavior. Current native builds send only `Using T3 Code`, `Using Claude`, or `Using Codex` in `details`. Every other field below needs new work, and some depends on telemetry that is still in research.

Interactive concept: [agentic-discord-presence.html](agentic-discord-presence.html). It uses fictional sample data and says so on screen.

Companion to [discord-display.md](discord-display.md) (field surface and privacy rules) and [vscode-presence.md](vscode-presence.md) (ecosystem patterns).

## Reference: what Discord actually draws

The PR #18346 screenshot (`/data/ubuntu/tmp/developer-presence/research/pr-18346-discord.png`) shows the profile "Playing" card:

```
Playing                                   ···
[T3 logo]  T3 Code                 ← application name (bold)
           In OpenMouse Project    ← details
           Claude Opus 5.5 · Idle  ← state
           🎮 1:26                 ← elapsed timer (green, Discord-rendered)
```

That PR used T3's own application ID, so the bold title read "T3 Code". **Our companion uses its own public application ID, registered as "Developer Presence"**, so the bold title and the member-list line ("Playing Developer Presence") always read "Developer Presence". The Social SDK documents `Activity::SetName` for overriding the title. Our raw-IPC `SET_ACTIVITY` path has not validated that, so the design does not rely on a dynamic title. The app identity therefore has to come from the large image and the `details` line.

## Information hierarchy

| Slot | Carries | Rule |
| --- | --- | --- |
| Title | "Developer Presence" (registered) | Constant. Do not assume an override works. |
| Large image | Observed app's logo (T3 Code / Claude / Codex) | Primary identity. Hover text = app name, plus the metric definition when usage is shown. |
| Small badge | **One** of: status (working / needs you / idle) or provider of the pinned thread | Only one slot exists, so the user picks one. Hide it when no data backs it. |
| `details` | App, plus an optional project alias | `Using T3 Code` by default; `T3 Code · Orbit Notes` when an alias is opted in. |
| `state` | The one metric the user chose to showcase | Workload or usage, never both. Missing → omit the line. |
| Timer | One fixed meaning per preset | Focus time for app-only, observed app session for rich presets. Never switch meanings silently. |
| Buttons (≤2) | Companion page, and optionally a public repo | Repo only when the project alias is shared and the repo is explicitly public. |

Design principle: **one card, one story.** Discord is not a dashboard. Don't rotate cards to cram in more metrics, and don't concatenate every metric into one line.

## Presets

All values are fictional.

**App only** (works today for all three apps; only the artwork and timer are new)
```
Developer Presence
Using Claude
🎮 24:18            (focus time)
```

**Workload** (T3 only; needs the T3 metadata adapter)
```
Developer Presence           [T3 logo + amber "!" badge]
Using T3 Code
2 running · 1 needs you
🎮 24:18            (observed app session)
```
Counts are top-level threads; delegated helpers are excluded. Badge priority is needs-you > working > idle.

**Usage** (T3 only; needs cumulative counters from the adapter)
```
Developer Presence
Using T3 Code
842K tokens this session              (cost off: default)
842K tokens this session · ~$1.84 API (cost on: may truncate; see Fit)
```
Context variant: `62% of context used`. Scope is locked to the pinned thread.

## Source confidence

The concept tags every outgoing field with one of these levels. Native settings should show the same levels in their preview.

| Level | Meaning | Fields |
| --- | --- | --- |
| **Shipped** | In current builds | `details = "Using <App>"` |
| **Companion only** | Needs only our own assets and config, no app telemetry | Large image, focus timer, companion button |
| **Needs T3 adapter** | A companion adapter still needs implementation. Thread-list MCP already exposes metadata; aggregate usage exists in a separate versioned RPC | State counts, model, project alias, status/provider badge, tokens, context %, cost |
| **Not observable yet** | No verified desktop observer | Anything beyond focus for Claude Desktop and Codex Desktop |

For Claude and Codex, the concept disables Workload and Usage and explains why. It never shows zeros or placeholders: missing data is omitted, not zero.

## Metric definitions (must appear in hover text or settings)

- **Cumulative processed tokens:** the sum over the selected scope, normalized so cached input and reasoning tokens are not double counted. Show it only if the adapter supplies real cumulative counters; never sum raw update events.
- **Context %:** current occupancy of one thread's window. It is a different metric and must never be labelled or summed as usage.
- **Estimated cost (`~$… API`):** the API-equivalent price of the counted tokens. It is **not** a subscription charge, and the UI must never call it "spent". Off by default.
- **Scopes:** pinned thread and observed app session are future adapter targets. The existing aggregate RPC can instead provide provider totals today, including activity outside T3. It does not alone establish either session scope. There is deliberately no "current desktop tab" scope, because the companion cannot observe which tab is open.

## Recommended UX options (native settings)

The settings screen and the card preview sit side by side, and the preview always reflects what will be sent.

1. **Desktop app:** a segmented control. Unavailable presets stay visible but disabled, with a one-line reason.
2. **Card shows:** App only / Workload / Usage, as a segmented control. Default is **App only** until the adapter exists, and **Workload** after it ships.
3. **Usage options** (only while Usage is selected): Metric (cumulative tokens / context window) and Scope.
4. **Small badge:** Status / Provider / None. Default is Status.
5. **Share toggles:** Project alias (off), Model (off), Est. API cost (off), Elapsed timer (on), Playful wording (off). Hide toggles that don't apply instead of showing them disabled.
6. **Buttons:** None (default) / Companion / Companion + public repo.
7. **Outgoing fields disclosure:** an exact list of every field and value, including hover text, with its source-confidence tag and "nothing else is sent".
8. **Fit check:** if `details` or `state` is truncated at Discord's card width, flag it inline and suggest dropping a detail or shortening the alias. In the concept, Usage with cost, model, and alias all enabled demonstrates this. Use a conservative 128-character compatibility cap and validate per-field Unicode rules on real clients.

Privacy defaults are inherited from discord-display.md. Project, model, and cost are hidden by default. Aliases are used instead of real names. Prompts, paths, titles, and command output are never sent. Exclusions apply before aggregation. Disable any aggregate that cannot honor project exclusions.

## Playful wording

Playful wording is opt-in and derived only from real metrics. If a number would be invented, the phrase is not shown.

| Plain | Playful |
| --- | --- |
| `3 running` | `Herding 3 agents` |
| `1 needs you` | `1 wants you` |
| `Idle` | `Agents napping` |
| `842K tokens this session` | `Crunched 842K tokens this session` |
| `62% of context used` | `Context tank 62% full` |

No scores, levels, streaks, or fake party sizes. Milestones such as "passed 1M tokens today" are acceptable only when they are computed from observed counters.

## Artwork

- Logos: the concept uses existing repo assets only (`assets/prod/logo.svg` from the T3 fork, and `claude-ai-icon.svg` / `openai_dark.svg` from its marketing harness icons). The Codex tile uses the **OpenAI mark illustratively**; final Codex artwork still needs confirmation and permission review.
- Status badges (working / needs you / idle) are new, simple companion-owned glyphs, not third-party logos. Export them as 1024² PNGs with lowercase keys (`status_working`, `status_attention`, `status_idle`, `provider_claude`, `provider_codex`).

## Prototype notes

- **Host contrast fix:** T3's preview host sets `:root{color-scheme:dark}` with a dark background while `prefers-color-scheme` reports light. The old root's `color-scheme: light dark` therefore picked light tokens, which made labels dark-on-dark. The concept now samples the computed background of the nearest opaque ancestor (via a 1×1 canvas, so any CSS color format works). It sets the root `color-scheme` from that luminance, falls back to the declared scheme and then the media query, and re-checks on host style or attribute changes. All colors are product-specific `light-dark()` tokens; no visualization tokens are used inside the mock.
- Logos are inlined as data URIs, so the file is fully self-contained.

## Open issues

- Validate `activity.name` override, `status_display_type`, and `*_url` fields over raw IPC on real clients before designing around them.
- The T3 adapter's capability manifest has to define which of state, model, context, cumulative usage, and cost are actually available, and their provenance.
- Provider badge with mixed-provider threads: the proposal is "provider of the pinned thread", with the badge hidden when nothing is pinned. This needs a decision.
- Check whether Discord truncation width differs between the profile popout, the full profile, and the member-list hover. The concept uses about 316px of card width.
- Discord does not show buttons to the user who owns the activity in some clients. Acceptance tests need a second account.

## Research reconciliation

The root review found an existing transcript-free `server.getUsageSummary` RPC. See [recommendations](recommendations.md) and [T3 telemetry](t3-telemetry-and-pr.md) for scope and authentication. The concept now explicitly distinguishes provider/day aggregates from future pinned-thread/session data. Native defaults should expose only supported scopes. All sample values remain fictional.

## Final preview validation

Root reviewed the full interactive document at 728px dark/light and 360px dark; a 360px light pass also exercised usage, API-cost labeling, provider-day scope, context scope, both desktop fallbacks, and workload restoration. All nine interaction/image/width checks passed with no console errors. These are concept checks, not live Discord or native-app validation.

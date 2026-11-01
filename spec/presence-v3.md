# Presence controls, version 3

Implementation contract for both native apps. It extends [T3 activity v2](t3-activity.md) and supersedes any rule there that conflicts. Shared, executable expectations are in [presence-v3-cases.json](presence-v3-cases.json); both test suites must load that file.

Design sources: [presence design](../docs/research/presence-design.md), [recommendations](../docs/research/recommendations.md), [harness workload](../docs/research/harness-workload.md).

## 1. Multiple T3 environments

Preview 3 followed one address and auto-discovered only local T3, so users whose work runs on a remote T3 saw a "Connected" status and no counts.

- Replace the single address with an ordered list of **T3 environments**. Each entry is `{ id, address, label }`, where `id` is a random UUID, `address` is the normalized MCP URL, and `label` is a user-editable local name (default: host name). Labels never leave the computer.
- Migration: a non-empty legacy `t3Address` becomes the first entry. If it was empty and local discovery previously succeeded, discovery adds the local entry on the next **Allow T3 activity**.
- Settings show each environment with its own status (Not connected / Opening sign-in / Connected / Sign in again / Couldn’t reach T3 Code), **Allow**, **Remove**, and a usage-permission state (§6). **Add environment** accepts a Copy MCP URL value or an origin and uses the existing URL validator.
- Every environment uses its own OAuth credential stored under its normalized MCP URL (existing keychain/credential-manager key format). Removing an entry deletes its credential.
- Poll every connected environment independently on the existing 15-second cadence with no overlap per environment. A failing environment contributes nothing and does not invalidate the others. The combined snapshot is fresh only if at least one environment produced a fresh snapshot; stale environments are dropped, not zeroed.
- Thread identity is `(environmentId, threadId)`. Duplicate rows inside one environment still collapse.
- The status line in the main window summarises: `Connected to 2 of 3 environments`. When an environment is connected but has never returned work, recovery text says: `Connected. No running tasks were found here — add the environment where your tasks run.`

## 2. Thread metadata

Decode these additional `t3_thread_list` fields; everything else stays ignored:

| Field | Use |
| --- | --- |
| `projectId` | Exclusions, aliases, pinned project |
| `model` | Optional model label (§5.4) |
| `parentThreadId` | Not published; used only to keep helpers attached to excluded parents |
| `createdAt`, `updatedAt` | Pinned-task picker ordering only |
| `title` | **Local settings picker only.** Never logged, persisted beyond the in-memory picker, or published |

Project names come from `t3_project_list` (add it to the allowlist; it requires only `orchestration:read`). Project titles appear only in the local alias editor/picker. `t3_project_list` is called with `{}` (plus `cursor` while one is returned); read `projects[].projectId` (or `id`) and `title` (or `name`). Calls remain allowlisted: `orchestrator_capabilities`, `t3_thread_list`, `t3_project_list`.

Exclusions apply before any aggregation: a thread whose `projectId` is excluded is dropped, and so is any helper whose `parentThreadId` resolves to an excluded thread.

## 3. Settings

All new fields decode with defaults so older settings files still load. Defaults preserve privacy: nothing new is shared until enabled.

| Key | Values | Default |
| --- | --- | --- |
| `t3Environments` | list (§1) | migrated |
| `cardStyle` | `appOnly`, `workload`, `usage` | `workload` |
| `usageMetric` | `tokens`, `context` | `tokens` |
| `usageScope` | `today`, `session`, `pinnedTask` | `today` |
| `tokenCategory` | `total`, `input`, `output` | `total` |
| `tokenRounding` | `compact`, `exact` | `compact` |
| `smallBadge` | `status`, `provider`, `none` | `status` |
| `shareProjectAlias` | bool | false |
| `projectAliases` | map `"{environmentId}/{projectId}" → alias` | empty |
| `shareModel` | bool | false |
| `shareEstimatedCost` | bool | false |
| `showElapsedTimer` | bool | true |
| `playfulWording` | bool | false |
| `buttons` | `none`, `companion`, `companionAndRepo` | `none` |
| `publicRepoUrl` | string | empty |
| `excludedProjects` | set of `"{environmentId}/{projectId}"` | empty |
| `pinnedTask` | `"{environmentId}/{threadId}"` or empty | empty |
| `pinnedProject` | `"{environmentId}/{projectId}"` or empty | empty |
| `selectionMode` | `followActiveApp`, `keepBackgroundVisible` | `keepBackgroundVisible` |

Availability (disabled options stay visible with a one-line reason):

- `workload` and `usage` require Show T3 activity and at least one connected environment. Reason: `Connect T3 Code to show work.`
- `usage` requires the usage permission on at least one environment (§6). Reason: `Allow token usage for an environment first.`
- `usageMetric = context` forces `usageScope = pinnedTask`. Reason shown under Scope: `Context is measured per task.`
- `usageScope = pinnedTask` requires a pinned task. Reason: `Pick a task to pin.`
- `usageScope = today` is unavailable while any project exclusion exists, because the provider aggregate cannot honor exclusions. Reason: `Today's totals can't skip excluded projects.`
- `shareEstimatedCost` only applies to `usage` + `tokens` + `today`; hide it otherwise.
- `companionAndRepo` requires `shareProjectAlias` and a valid `publicRepoUrl`.

## 4. Selection

- `keepBackgroundVisible` is the preview 3 behavior: fresh unfinished T3 work wins over another foreground app.
- `followActiveApp`: the foreground supported app always wins; T3 rich data only appears while T3 is the foreground or the only running supported app.
- Featured tool: unchanged winner rules from v2, computed over the combined snapshot.
- Pinned project (if set and present in the snapshot) restricts the counts to that project. Without a pin the alias shown is that of the project with the most running work for the featured tool; ties keep the previous project; none → no alias.

## 5. Card formatting

The formatter is a pure function of `(settings, combined snapshot, usage snapshot, timer state, foreground app, now)`. It returns the Discord activity plus a list of preview rows `{ field, value, source }`, where `source` is one of `Deskchirp`, `T3 Code`, `T3 usage`.

### 5.1 Details

| Situation | `details` |
| --- | --- |
| App only, or no fresh work | `Using T3 Code` |
| Rich card, alias shared and available | `T3 Code · {alias}` |
| Rich card, otherwise | `T3 Code · {tool}` |

Aliases: remove control characters, collapse whitespace runs to one space, trim, keep the first 32 characters, trim again. Empty alias = no alias.

### 5.2 State

Workload, plain (unchanged from v2): `2 tasks · 4 helpers`, `1 task waiting`, `3 helpers starting`.

Workload, playful:

| Phase | Text |
| --- | --- |
| Running | `Herding {n} agent(s)` where n = running tasks + running helpers |
| Waiting | `{n} agent(s) on standby` |
| Starting | `Warming up {n} agent(s)` |

Usage, tokens: `{amount} tokens {scope}` (`1 token` when the amount is exactly one) where scope is `today`, `this session`, or `on this task`. Playful prefix: `Crunched {amount} tokens {scope}`. With cost: append ` · ~${cost} API est.`

Usage, context: `{p}% of context used`; playful `Context tank {p}% full`. `p` = floor(used × 100 / max); omit when `max` is missing or zero.

If the selected usage value is unavailable, stale, or partial, fall back to the workload state for that card; never print zero or a guess.

Model (`shareModel`): append ` · {model}` to the workload state. Model label = the model of the featured tool's most recently updated running top-level thread (pinned task wins). Accept only `[A-Za-z0-9 ._:/-]`, at most 40 characters; otherwise omit.

### 5.3 Numbers

- `compact`: `< 1000` → integer. Otherwise pick the unit 1e3/1e6/1e9 (`K`/`M`/`B`) and round half up using integer arithmetic: tenths = (n × 10 + unit / 2) ÷ unit. If tenths < 1000, print one decimal (drop a trailing `.0`); otherwise print whole units = (n + unit / 2) ÷ unit. If whole units reach 1000, move to the next unit and repeat. Examples: `999`, `1K`, `1.1K` (1050), `99.9K`, `100K` (99950), `842K`, `1M` (999999), `1.2M`, `12.5M`, `150M`, `3.4B`.
- `exact`: integer with comma thousands separators (invariant): `842,112`.
- Cost: round half up to cents first. Below $10 → two decimals; otherwise round half up to whole dollars. `~$1.84`, `~$10` (9.996), `~$23`.
- Token categories: `input` = uncached input + cached input + cache creation; `output` = output (reasoning is already inside output; never add it); `total` = input + output.

### 5.4 Assets

- Large image: `t3_code` with hover `T3 Code`. In usage mode (when a usage value is shown) the hover is `T3 Code · {definition}`, with definitions `Tokens counted today across providers, including use outside T3` / `Tokens counted since this work session started` / `Tokens counted for the pinned task` / `How full the pinned task's context window is`. Hover text is capped at 128 characters.
- Small badge `status`: running → `status_working` / `Working`; waiting → `status_attention` / `Waiting`; starting → `status_working` / `Starting`. No fresh work → no badge.
- Small badge `provider`: the featured tool's `harness_*` key with the tool's public name; tools without artwork → no badge.
- `showAppIcons = false` removes all assets, as before.
- Artwork URLs move to the `artwork-v2` tag, which adds `status_working`, `status_attention`, and `status_idle`. The tag is one constant per app.

### 5.5 Timer

- One meaning per card style. App only: when the shown app became the shown app. Rich cards: the work session — starts at the first fresh snapshot with unfinished work, survives running ↔ waiting ↔ starting, and ends after 120 seconds with no unfinished work (or when activity sharing stops). Rich card without fresh work falls back to the app-only clock.
- Send as `timestamps.start` in Unix seconds. `showElapsedTimer = false` omits it.
- The session scope for usage (`this session`) uses the same work-session start.

### 5.6 Buttons

- `companion`: `{ label: "Get Deskchirp", url: "https://github.com/geekynasir/deskchirp-discord" }`.
- `companionAndRepo`: adds `{ label: "View project", url: publicRepoUrl }`, only while an alias is shown.
- `publicRepoUrl` must match `https://github.com/{owner}/{repo}` (`[A-Za-z0-9-]{1,39}` / `[A-Za-z0-9._-]{1,100}`), no query/fragment/extra path. Invalid → button omitted and settings show `Use a public GitHub repository link.`
- Labels ≤ 32 characters, at most two buttons.

### 5.7 Fit check

- Hard cap: `details`, `state`, and hover texts are cut to 128 characters (ending in `…`) before sending.
- Soft fit: when `details` or `state` is longer than 34 characters the preview shows `May be cut off on Discord. Try a shorter alias or turn off a detail.` next to that row. This does not change what is sent.

### 5.8 Privacy invariants

Never publish: titles, prompts, paths, project names (only user-written aliases), environment labels or addresses, account or instance names, unknown driver strings, token/cost values that are stale, partial, or computed from excluded data.

## 6. Token usage permission

The MCP permission (`orchestration:read`) exposes no token data. Token totals, context percentage, and estimated cost come from T3's app interface, which needs `diagnostics:read`. See [usage transport](t3-usage-transport.md).

- Per environment, **Allow token usage** is a separate opt-in. It explains: `T3 needs a one-time pairing code from that computer. Run the command below there, then paste the code. ` followed by the exact command and the permission wording from the transport spec.
- The resulting credential is stored in secure storage under `deskchirp-usage:{normalized origin}`, never in settings.
- Pairing codes are accepted as a bare code or pairing URL; this is not an access-token paste field.
- Usage polling runs every 60 seconds while `cardStyle = usage` and the card is eligible; otherwise it stops.
- Usage snapshot TTL: 180 seconds.
- Removing the environment or choosing **Remove token access** deletes the credential.

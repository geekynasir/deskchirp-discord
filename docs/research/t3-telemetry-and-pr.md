# T3 Code Telemetry, Community PR #18346, and Rich Presence Research

Researched on 2026-10-31. This document synthesizes deep research into upstream T3 Code telemetry, the state and architecture of community PR #18346, Ideas Discussion #18348, VS Code presence precedents, and technical feasibility for the independent native companion (`developer-presence`).

---

## 1. Executive Summary & Scope

* **Primary Sources Inspected:**
  * GitHub upstream: `pingdotgg/t3code` PR #18346 (read-only fresh state, timeline, reviews, comments, and release assets).
  * GitHub upstream: `pingdotgg/t3code` Ideas Discussion #18348 (read-only approval status and community discussion).
  * Local repository: `/data/ubuntu/projects/t3code-discord-presence` (contracts, server orchestration, usage aggregation, MCP services, and PR #18346 source ref `pr-18346`).
  * Local companion: `/data/ubuntu/projects/developer-presence` (architecture, specs, validation docs, and native build artifacts).
  * Community extensions: `iCrawl/discord-vscode` and `leonardssh/vscord` (VS Code Rich Presence architectures and privacy models).
* **Core Distinction:** Documented facts and code structures are strictly distinguished from analytical inferences. Stored internal server state is distinguished from externally accessible metadata.
* **Key Findings:**
  1. **Community PR #18346 Status:** Remains technically `OPEN` on GitHub despite a maintainer bot comment stating an intention to close it for lack of prior approval. Review bots (Macroscope and CodeRabbit) raised 5 correctness/security issues (including a CWE-359 privacy leak and connection races), with CodeRabbit's reported issues addressed in subsequent commits (`2b568e6`, `c717b24`), leading to its approved review. This does not establish that every reviewer concern is resolved.
  2. **Upstream Discussion #18348 Status:** Maintainer scope approval is **PENDING / NOT GRANTED**. No maintainer has replied to Discussion #18348.
  3. **T3 Telemetry:** T3 has an existing transcript-free aggregate RPC, `server.getUsageSummary`, guarded by `diagnostics:read`. It returns provider/model/time-window totals and cost provenance, not exact selected-thread totals. The public MCP thread-list tool has no usage fields. A companion adapter must validate RPC authentication, version compatibility, attribution, and source freshness.
  4. **Cost Accounting:** T3 differentiates `providerReported` (a cost supplied by the provider; not independent billing verification) from `modelPriced` (estimated via LiteLLM pricing tables). Flat-rate subscription users (e.g., ChatGPT Plus, Claude Pro) report $0 or estimated rates, requiring explicit UI disambiguation.
  5. **Companion Strategy:** The independent native companion should maintain its privacy boundary by implementing a tiered telemetry adapter model.

---

## 2. Community PR #18346 Audit (`pingdotgg/t3code`)

### 2.1 Fresh State & Metadata
* **Repository:** `pingdotgg/t3code`
* **Pull Request:** [#18346](https://github.com/pingdotgg/t3code/pull/18346)
* **Title:** `feat(desktop): add opt-in Discord Rich Presence`
* **Author:** `snekxs`
* **State:** `OPEN` (Created: `2026-10-11T20:52:19Z`, Updated: `2026-10-11T21:11:40Z`, `closedAt: null`, `mergedAt: null`).
* **Labels:** `vouch:unvouched`, `size:XXL` (1,000+ line changes).
* **Head Commits:**
  1. `423ccb5f2`: Initial implementation `feat(desktop): add opt-in Discord Rich Presence`.
  2. `2b568e624`: `fix(discord): prevent stale presence and connection leaks`.
  3. `c717b24ac`: `fix(discord): honor saved fields on startup`.

### 2.2 Proposed User Behavior & Architecture
* **Opt-In Setting:** Off by default under **Settings → Integrations → Discord** (`apps/web/src/components/settings/IntegrationsSettings.tsx`).
* **Granular Field Controls:** Users independently toggle four discrete fields:
  * `project` (default on): Project name of active thread.
  * `activity` (default on): High-level agent status across environments.
  * `model` (default off): Model display label.
  * `elapsed` (default off): Duration of the desktop integration session.
* **Component Architecture:**
  * **Renderer Coordinator:** `apps/web/src/components/DiscordPresenceCoordinator.tsx` tracks active routes, views `environmentShell`, and computes a compact `DesktopDiscordPresence` summary (`projectName`, `modelLabel`, `workingThreads`, `attentionThreads`).
  * **Desktop IPC Bridge:** Electron preload and main IPC channels (`setDiscordPresence`, `getDiscordPresenceStatus`, `onDiscordPresenceStatus` in `apps/desktop/src/ipc/methods/discordPresence.ts`).
  * **Desktop Service:** `apps/desktop/src/integrations/DesktopDiscordPresence.ts` manages desired state, serializes socket updates via `Semaphore.make(1)`, and gates connections on client ID presence.
  * **Transport Client:** `apps/desktop/src/integrations/discord/discordIpc.ts` implements raw Unix domain socket and Windows named pipe framing (`discord-ipc-0`..`9`) without external Discord SDK dependencies.

### 2.3 Review History & Correctness Findings
1. **Macroscope Bot Review (`macroscopeapp[bot]`):**
   * Verdict: **Not approved** (5 blocking correctness issues at or above repository minimum severity).
   * Cited concerns: Large surface spanning renderer state, Electron IPC, local socket transport, static analysis suppressions, opt-out behavior, connection races, and socket cleanup.
2. **Maintainer Action (`juliusmarminge` via ChatGPT Codex Connector):**
   * Commented on `2026-10-11T20:55:26Z`: *"This adds a new Discord integration that publishes project, model, and agent activity. The PR explicitly states that maintainer approval has not been obtained. The contribution guide requires approval of a new feature's direction and scope, so I'm closing this PR. Please propose the integration in an Ideas discussion and obtain explicit scope approval before requesting reconsideration."*
   * *Observation:* The API reports OPEN despite the closure wording in the comment; the reason for that discrepancy is not established here.
3. **CodeRabbit Review Findings & Fixes (`coderabbitai[bot]`):**
   * **Connection Concurrency Race (Major):** Multiple asynchronous connect attempts could overwrite socket references, leaking open sockets and allowing stale presence to overwrite user opt-outs (`set(null)`).
     * *Fix in `2b568e6`:* Added `Semaphore.make(1)` around `applyOnce`, verified `desired` state before and after `ensureClient`.
   * **Information Disclosure / CWE-359 (Major):** At startup, `DesktopDiscordPresence` initialized `fields` to `DEFAULT_DISCORD_PRESENCE_FIELDS` (including `"project"`). Settings hydration from disk did not re-trigger configure, causing project names to be broadcast even when the user had saved a privacy choice omitting projects.
     * *Fix in `c717b24`:* Initialized `fields` Ref directly from `clientSettings.get` during service construction before the first presence update.
   * **Route Navigation Stale State (Minor):** Moving to routes without an active environment (such as Settings or Home) failed to clear `activeThread`, leaving stale project and model names in the presence payload.
     * *Fix in `2b568e6`:* Added `EMPTY_ACTIVE` reset in `DiscordPresenceCoordinator.tsx` whenever `activeEnvironmentId` is undefined or not in `environmentIds`.
   * *Result:* CodeRabbit marked all issues addressed and issued an **APPROVED** review state on commit `c717b24ac`.

### 2.4 Evidence & Artifact Links
PR #18346 provided evidence hosted as release assets on `snekxs/t3code`:
* [Project, model, activity, and elapsed time presence](https://github.com/snekxs/t3code/releases/download/discord-presence-evidence-20261011/project-model-activity.png)
* [Idle presence (redundant fallback removed)](https://github.com/snekxs/t3code/releases/download/discord-presence-evidence-20261011/idle-presence.png)
* [Integration settings and connection status UI](https://github.com/snekxs/t3code/releases/download/discord-presence-evidence-20261011/integration-settings.png)
* [Earlier output showing redundant "Using T3 Code" line](https://github.com/snekxs/t3code/releases/download/discord-presence-evidence-20261011/before-fallback-line.png)

---

## 3. Upstream Discussion #18348 Approval Status (Read-Only)

* **Repository:** `pingdotgg/t3code`
* **Discussion:** [#18348](https://github.com/pingdotgg/t3code/discussions/18348)
* **Title:** *Optional desktop Discord Rich Presence (scope approval request)*
* **Category:** Ideas
* **Created:** `2026-10-11T21:02:18Z` by `snekxs`
* **Community Contributions:**
  * `IndraTensei` (2026-10-21): Verified PR #18346 on Windows 11 (`\?\pipe\discord-ipc-0`); noted that if Discord starts or restarts after T3 Code, presence is not restored until an event triggers an update. Implemented periodic reconnection on a personal rebase branch.
  * `geekynasir` (2026-10-30, Comment `#19293472`): Inquired whether maintainers would accept a tightly scoped, desktop-only PR against main, or whether an independent native companion (SwiftUI / WinUI) consuming authenticated read-only interfaces was preferred.
* **Approval Verdict:** **NO MAINTAINER RESPONSE / APPROVAL PENDING**. As of 2026-10-31, maintainers have neither approved the scope in Discussion #18348 nor reconsidered PR #18346. Any implementation within the core T3 Code repository remains unapproved under `CONTRIBUTING.md`.

---

## 4. Logos, Branding & Discord Application Asset Strategy

### 4.1 PR #18346 Asset Architecture
* **Application ID:** Read from `T3CODE_DISCORD_CLIENT_ID` process environment or compile-time `__T3CODE_BUILD_DISCORD_CLIENT_ID__` (wired to repository secret `DISCORD_CLIENT_ID`).
* **Channel Asset Mapping (`presenceActivity.ts`):**
  * `Dev` stage: `"blueprint"`
  * `Nightly` stage: `"nightly"`
  * `Alpha` stage: `"black"`
* **Large Image Hover Text:** Clamped to `${branding.label} ${branding.version}` (e.g., `T3 Code v0.0.46`).
* **Fallback Removal:** PR #18346 explicitly removed the details fallback line `"Using T3 Code"` because Discord automatically displays the registered application name at the top of the Rich Presence card.

### 4.2 Discord Asset Rules & Constraints
* **Dimensions & Formats:** 1024x1024 recommended; PNG, JPEG, WebP (static only; animated WebP/GIF not supported for uploaded application assets).
* **Asset Keys:** Lowercase alphanumeric strings (maximum 300 uploaded assets per application ID).
* **Text Limits:** Use a conservative 128-character compatibility cap for text; validate field-specific and Unicode behavior against Discord clients.
* **Badging Slots:** Discord provides exactly one `small_image` slot overlaying the `large_image`. A client cannot simultaneously display multiple badges (e.g., provider mark and working status); it must choose between them or compose icons.

### 4.3 Recommendations for Independent Companion (`developer-presence`)
1. **Public Client ID Ownership:** The companion cannot use an official T3 Code Discord application ID because it is maintained independently and also monitors Claude Desktop and Codex Desktop. The companion must register its own Discord Application (e.g., `Developer Presence` or configurable custom client ID).
2. **Multi-App Asset Pack:** Upload distinctive, high-contrast assets to the companion's Discord Application:
   * **App Logos (Large Image):** `app_t3code`, `app_claude`, `app_codex`, `app_unknown`.
   * **Status Badges (Small Image):** `status_working` (green/pulse), `status_attention` (amber/alert), `status_idle` (slate), `status_paused` (gray).
3. **Card Formatting Patterns:**
   * Line 1 (`details`): `In <ProjectName>` or `T3 Code · <Model>` (if project hidden).
   * Line 2 (`state`): `<StatusSummary>` (e.g., `Running 2 agents` or `Idle`) joined with model or elapsed time.
   * Large Tooltip: `<AppName> · <ObservedVersion>` (e.g., `T3 Code 0.0.46 (via Local Shell)`).
   * Small Tooltip: `Working: 2 · Waiting: 1`.

---

## 5. VS Code Rich Presence Inspiration (`iCrawl` & `vscord`)

Inspection of leading VS Code Discord presence implementations (`iCrawl/discord-vscode` and `leonardssh/vscord`) reveals mature patterns for developer presence and privacy management:

### 5.1 Architecture & Transport
* **Layered Pipeline:** Strict separation between Editor State Listeners, Privacy/Filter Gate, Debounced Formatter, and Local Socket Transport.
* **Transport Reliability:** Iterates across IPC endpoints 0 to 9 (`\?\pipe\discord-ipc-n` on Windows, `$XDG_RUNTIME_DIR/discord-ipc-n` or `/tmp/discord-ipc-n` on Unix). Implements automatic reconnect backoff when Discord restarts.
* **Rate-Limit Coalescing:** Updates are throttled to a minimum 5-second interval to avoid Discord IPC rate-limiting and UI flickering.

### 5.2 Privacy Controls Comparison
| Feature | `iCrawl/discord-vscode` | `leonardssh/vscord` | Recommended Companion Equivalent |
| :--- | :--- | :--- | :--- |
| **Workspace Exclusion** | `workspaceExcludePatterns` (globs) | `vscord.ignore.workspaces` (array) | Per-project privacy blacklist / ignore list |
| **Repository Ignore** | N/A (relies on workspace) | `vscord.ignore.repositories`, `ignore.organizations` | Git remote / repo URL suppression |
| **Privacy Mode** | `removeDetails`, `removeLowerDetails` | `vscord.app.privacyMode.enable` | One-click "Stealth Mode" (hides project, repo, model) |
| **Workspace Masking** | Custom text template (`lowerDetailsEditing`) | `vscord.ignore.workspacesText` | Configurable alias or generic `"Private Workspace"` |
| **Idle Behavior** | `idleTimeout` (seconds) | `idle.timeout`, `idle.disconnectOnIdle` | Option to either show `"Idle"` or clear Discord status |
| **Elapsed Timer** | `removeTimestamp` | Configurable per mode | Option to hide session elapsed time |
| **Repository Button** | `removeRemoteRepository` | Configurable buttons | Never expose private Git URLs; link only public repos |

---

## 6. T3 Code Telemetry Audit: Stored Internal Data vs External Metadata

A rigorous audit of `/data/ubuntu/projects/t3code-discord-presence` reveals how tokens, cost, and threads are modeled internally versus what is exposed to outside consumers.

### 6.1 Internal Token Usage Schemas (`packages/contracts/src/`)

#### 6.1.1 Thread-Level Snapshot: `ThreadTokenUsageSnapshot`
Located in `packages/contracts/src/providerRuntime.ts` (lines 30–54):
```typescript
export const ThreadTokenUsageSnapshot = Schema.Struct({
  usedTokens: NonNegativeInt,                       // Current context window occupancy
  totalProcessedTokens: Schema.optional(NonNegativeInt), // Cumulative tokens across entire thread
  maxTokens: Schema.optional(PositiveInt),          // Model context window limit
  inputTokens: Schema.optional(NonNegativeInt),     // Total input tokens
  cachedInputTokens: Schema.optional(NonNegativeInt), // Cached input read tokens
  outputTokens: Schema.optional(NonNegativeInt),    // Total output tokens
  reasoningOutputTokens: Schema.optional(NonNegativeInt), // Thinking/reasoning tokens
  lastUsedTokens: Schema.optional(NonNegativeInt),  // Previous turn context occupancy
  lastInputTokens: Schema.optional(NonNegativeInt),
  lastCachedInputTokens: Schema.optional(NonNegativeInt),
  lastOutputTokens: Schema.optional(NonNegativeInt),
  lastReasoningOutputTokens: Schema.optional(NonNegativeInt),
  toolUses: Schema.optional(NonNegativeInt),
  durationMs: Schema.optional(NonNegativeInt),
  compactsAutomatically: Schema.optional(Schema.Boolean),
  autoCompactThreshold: Schema.optional(PositiveInt),
  cost: Schema.optional(
    Schema.Struct({
      amount: Schema.Number.check(Schema.isFinite()),
      currency: TrimmedNonEmptyStringSchema.check(Schema.isMaxLength(32)),
    }),
  ),
});
```

#### 6.1.2 Turn-Level Usage: `TurnTokenUsage`
Located in `packages/contracts/src/providerRuntime.ts` (lines 56–78):
```typescript
// Input includes cache reads/writes; output includes reasoningTokens subset.
const TurnTokenUsageCommonFields = {
  usageScope: Schema.Literal("main_agent"),
  cachedInputTokens: Schema.optional(NonNegativeInt),
  cacheCreationTokens: Schema.optional(NonNegativeInt),
  reasoningTokens: Schema.optional(NonNegativeInt),
  hasSubagents: Schema.Boolean,
};

export const TurnTokenUsage = Schema.Union([
  Schema.Struct({
    ...TurnTokenUsageCommonFields,
    usageStatus: Schema.Literal("complete"),
    inputTokens: NonNegativeInt,
    outputTokens: NonNegativeInt,
  }),
  Schema.Struct({
    ...TurnTokenUsageCommonFields,
    usageStatus: Schema.Literals(["partial", "unavailable"]),
    inputTokens: Schema.optional(NonNegativeInt),
    outputTokens: Schema.optional(NonNegativeInt),
  }),
]);
```
* **Arithmetic Invariant:** `inputTokens = uncachedInputTokens + cachedInputTokens + cacheCreationTokens`.
* **Output / Reasoning Invariant:** `reasoningTokens` is a subset of `outputTokens`, **never added on top** of output tokens.

#### 6.1.3 Historical Aggregation: `UsageBucket` & `UsageTokenTotals`
Located in `packages/contracts/src/usage.ts`:
* `UsageTokenTotals`: `uncachedInputTokens`, `cachedInputTokens`, `cacheCreationTokens`, `outputTokens`, `reasoningTokens`.
* `UsageCategoryCost`: `input`, `cacheRead`, `cacheWrite`, `output`.
* `UsageBucket`: Binned by day/hour, provider, model; tracks `totals`, `costUsd`, `cacheSavingsUsd`, `categoryCostUsd`, `costSource` (`providerReported` | `modelPriced` | `unpriced`), `records`, and `sessions`.

### 6.2 Actual Cost vs. Estimated Pricing (`apps/server/src/usage/usagePricing.ts`)
* **Cost Sources:**
  1. `providerReported`: Provider directly reported a USD cost in the turn/session payload; this alone does not verify the user's bill (`reportedCostUsd`).
  2. `modelPriced`: Server estimated cost by multiplying token categories by rates from LiteLLM's `model_prices_and_context_window.json`.
  3. `unpriced`: Model not found in pricing tables and no cost reported by provider.
* **Pricing Math:**
  $$	ext{List Cost} = (	ext{uncachedInput} 	imes R_{	ext{in}}) + (	ext{cachedInput} 	imes R_{	ext{cacheRead}}) + (	ext{cacheCreation} 	imes R_{	ext{cacheWrite}}) + (	ext{output} 	imes R_{	ext{out}})$$
* **Subscription Warning:** Many developer models run on flat-rate monthly subscriptions (e.g. ChatGPT Plus/Team, Claude Pro/Team, GitHub Copilot). Their reported cost is $0 or reflects theoretical API equivalence. **A companion must never present estimated API cost as actual money charged to the user.**

### 6.3 External Interface Boundaries: The Privacy Barrier

#### 6.3.1 Read-Only MCP Orchestration Tools (`apps/server/src/mcp/`)
Documented in `docs/user/outside-agents.md` for external agents connecting via HTTP + OAuth (`/mcp`):
1. `t3_thread_list`:
   * Returns: `OrchestratorMcpThreadListItem[]`.
   * Fields: `threadId`, `projectId`, `title`, `status`, `model`, `itemCount`, `parentThreadId`, `relationshipToParent`, `settled`, `createdAt`, `updatedAt`.
   * **Telemetry Payload:** **NO token usage, NO context occupancy, NO cost data.**
2. `t3_thread_read`:
   * Returns: `OrchestratorMcpThreadDetail`, `recentRuns: OrchestratorMcpThreadRun[]`, and `items: OrchestratorMcpThreadTimelineItem[]`.
   * **Telemetry Payload:** `recentRuns` only contains `runId`, `ordinal`, `status`, `model`, and timestamps. It contains **NO token usage or cost**.
   * **Privacy Leak:** The `items` array contains `text`—the **complete message bodies, user prompts, and agent responses**.

#### 6.3.2 Shell Payloads & WebSocket RPC
1. **Shell Stream (`subscribeShell` / `OrchestrationV2ThreadShell`):**
   * Carries high-level thread status, active run ID, model selection, error codes, and pending requests.
   * Carries an optional `goal: OrchestrationV2ProviderGoal` (which has optional `tokensUsed` / `tokenBudget` specifically for Codex goals), but **carries NO general token usage, session totals, or cost**.
2. **Full Projection (`orchestration.getThreadProjection`):**
   * Returns `OrchestrationV2ThreadProjection`.
   * Does contain `providerThreads[].contextUsage` (`ThreadTokenUsageSnapshot`) and `providerTurns[].turnTokenUsage` (`TurnTokenUsage`).
   * **Privacy Barrier:** It simultaneously returns the complete conversation history: `messages: OrchestrationV2ConversationMessage[]`, `turnItems: OrchestrationV2TurnItem[]`, and `visibleTurnItems`.

#### 6.3.3 Existing transcript-free aggregate RPC (review correction)

`packages/contracts/src/rpc.ts` declares `server.getUsageSummary`; `apps/server/src/ws.ts` dispatches it to `UsageService.readSummary`. `apps/server/src/auth/RpcAuthorization.ts` requires **diagnostics:read**. This is a versioned application RPC, not a documented third-party MCP tool.

`UsageSummaryInput` accepts day bounds, an IANA time zone, optional hourly resolution and time bounds, and `awaitRefresh`. Contract version 6 returns buckets grouped by day/hour, provider, and model, with token categories, `costUsd`, `costSource`, pricing freshness, and source scan status. It contains **no conversation bodies**.

Important limits:
- The service scans configured provider transcript directories. Totals can include activity outside T3 and are not automatically attributable to the foreground app, selected project, or pinned thread.
- Buckets do not expose the per-thread IDs needed to filter to one selected session. Label a validated aggregate **Provider usage today**, rather than **This T3 session**.
- Optional `sourcePath` and source host/home/volume fingerprints are private. Never include them in Discord payloads.
- Use `sources[].distinctSessions` for each non-overlapping source; bucket session counts double-count sessions spanning models/days. Deduplicate overlapping hosts/sources before summing across environments.
- `partial`, `missing`, `failed`, `refreshing`, and pricing `unavailable` need explicit handling. Missing data is not zero.
- Hourly requests require valid time instants and an interval greater than zero and no longer than 24 hours.
- Authentication, permission breadth, protocol stability, and actual desktop/provider coverage remain unvalidated for a companion. Do not silently request broader access.

A small metadata-only MCP wrapper could expose these existing aggregates if maintainers approve it. A narrower usage endpoint may still be desirable for exact pinned-thread or project-filtered totals. A new accounting backend is **not** required to investigate aggregate usage.

### 6.4 Thread Counting: Top-Level vs. Subagents
* **Identification:**
  * Subagents are identified by `thread.lineage.relationshipToParent === "subagent"`; a parent ID alone is insufficient because ordinary forks also have parents.
  * In `OrchestratorMcpThreadListInput`, `includeSubagents: false` by default.
* **PR #18346 Filter Logic:**
  * In `DiscordPresenceCoordinator.tsx`:
    ```typescript
    if (thread.lineage.relationshipToParent === "subagent") continue;
    ```
  * Subagents are excluded from `workingThreads` and `attentionThreads` to prevent task counts from artificially inflating when an agent spawns child subagents.
* **Turn Usage Scope:**
  * In `TurnTokenUsage`, `usageScope` is strictly `"main_agent"`, with a boolean flag `hasSubagents: true|false`. Subagent tokens are accounted for in separate subagent threads.

### 6.5 Multi-Environment Deduplication and Freshness
* **Environment Architecture:** T3 Code clients connect simultaneously to multiple environments (local machine, remote VPS, dev containers) tracked in `environmentIds`.
* **State Freshness:**
  * The web renderer maintains reactive atoms per environment (`environmentShell.stateValueAtom(environmentId)`).
  * If a remote environment disconnects or is removed, its counts must be cleared. PR #18346 resets `activeThread` to `EMPTY_ACTIVE` when navigating away from an environment.
* **Historical Dedup:**
  * Server-side `UsageAggregator` uses `UsageSourceFingerprint` (`hostId`, `provider`, `resolvedHomePath`, `volumeId`) and `dedupeKey` to prevent double-counting when transcripts are copied across git branches or resumed sessions.

---

## 7. Feature Feasibility Matrix

| Capability | Existing native builds | T3 metadata adapter (proposed) | Usage adapter (proposed) |
| :--- | :--- | :--- | :--- |
| App identity | T3, Claude, Codex | Same | Same |
| Workload/model/project | No | Thread-list metadata; user-pinned selection | Same |
| Token/cost totals | No | No usage in MCP thread list | Existing diagnostics RPC offers provider/time-window aggregates |
| Exact selected-thread usage | No | No | Needs a validated narrower metadata source |
| Upstream code change | No | No for documented MCP consumption | Not necessarily for RPC evaluation; new public wrapper requires approval |
| Privacy boundary | App focus is disclosed | Thread metadata is read; redact titles/projects | No message bodies, but paths/fingerprints remain private locally |

## 8. Recommendations

### 8.1 For the Independent Companion (`developer-presence`)
1. **Ship Phase 1 First:** Release and validate the foreground-only builds on real user hardware (verifying macOS `MenuBarExtra` and Windows WinUI 3 notification area). Do not delay the stable foundation for complex telemetry.
2. **Implement Phase 2 via `t3_thread_list`:**
   * Connect to T3's documented `/mcp` endpoint using OAuth read-only permissions.
   * Poll `t3_thread_list` at a conservative interval (e.g., every 15–30 seconds) or leverage WebSocket shell notifications.
   * Extract high-level workload: interpret the actual status/run schema and count confirmed active work, filtering out `relationshipToParent === "subagent"`. Do not assume every coarse waiting status means an approval request.
   * Display workload cards: `T3 Code · 2 running agents`.
3. **Do NOT Scrape Message Bodies for Telemetry:**
   * Avoid calling `t3_thread_read` or `orchestration.getThreadProjection` to obtain token counts. Ingesting full user messages, prompts, and tool outputs into a background presence companion creates an unacceptable security and privacy blast radius.
4. **Adopt VS Code Privacy Patterns:**
   * Implement project ignore lists (`ignoredProjects: ["secret-*"]`).
   * Provide a global "Stealth Mode" toggle in native settings.
   * Support "Disconnect on Idle" in addition to showing "Idle".

### 8.2 For Upstream T3 Code & PR #18346
1. **Keep PR #18346 Desktop-Only and Minimal:** If maintainers approve the scope in Discussion #18348, PR #18346's current form (after CodeRabbit fixes `2b568e6` and `c717b24`) represents a clean in-tree implementation that adheres to T3 architectural conventions.
2. **Evaluate the existing aggregate RPC, then propose only missing scope:**
   * First validate `server.getUsageSummary` for explicitly labeled provider/day totals. If exact per-thread telemetry is desired, propose a dedicated `thread.usage.get` or `mcp_thread_usage` endpoint that returns `ThreadTokenUsageSnapshot` and `TurnTokenUsage` **without** conversation `messages` or `turnItems`.

---

## 9. Unknowns & Open Questions

1. **Maintainer Appetite:** Will T3 Code maintainers ever approve desktop Rich Presence in core, or do they view all presence integrations as third-party companion territory?
2. **Discord Name Overrides over Raw IPC:** Does Discord's desktop IPC transport reliably accept `Activity::SetName` overrides on raw socket frames without registering a separate application ID in Discord's developer portal?
3. **Claude & Codex Desktop Companion Hooks:** While T3 Code exposes an MCP HTTP server, Claude Desktop and Codex Desktop currently lack identical local HTTP telemetry servers. Deeper presence for Claude and Codex needs independently validated adapters; app-server and CLI interfaces must not be assumed to observe running desktop sessions.
4. **Energy & Battery Footprint:** Polling remote MCP endpoints over HTTPS on battery-powered MacBooks has not yet been benchmarked against AppKit foreground event observation.

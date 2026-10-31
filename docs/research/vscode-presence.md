# VS Code Discord Presence Ecosystem Research: Architectural Insights, Primary Source Analysis, and Recommendations for Deskchirp

**Date:** 2026-10-31
**Author:** Research Sub-Agent
**Target Repository:** `the Deskchirp checkout`
**Context:** Independent native SwiftUI (macOS) and WinUI 3 (Windows) desktop companion. Currently implements foreground application detection (`Using T3 Code`, `Using Claude`, `Using Codex`). Richer metadata, telemetry, and external adapter capabilities are active proposals.

---

## 1. Executive Summary & Purpose

This research report analyzes the established Visual Studio Code Discord Rich Presence ecosystem to guide feature expansion, privacy architecture, asset hosting, and telemetry decisions for the independent **Deskchirp** companion application.

Traditional code editor extensions (e.g., `iCrawl/discord-vscode` and `LeonardSSH/vscord`) model a continuous, in-process stream of user buffer operations: active filename, programming language syntax, line/column coordinates, Git branch, and linter diagnostic counts. In contrast, modern agentic coding environments (such as T3 Code, Claude Desktop/CLI, and Codex Desktop/CLI) operate on an asynchronous task paradigm: planning/thinking, autonomous tool execution (file edits, terminal commands), user review pauses, multi-agent delegation, and token/context budgets.

By examining primary source code, configuration schemas, marketplace registries, and community pull requests—specifically community PR [#18346](https://github.com/pingdotgg/t3code/pull/18346) in `pingdotgg/t3code`—this report distinguishes between:
1. **Editor-specific mechanisms** that depend entirely on internal buffer APIs (`vscode.TextDocument`, `vscode.languages.getDiagnostics`, cursor selections) and cannot or should not be emulated by an external companion.
2. **Transferable design patterns** (Git remote sanitization, URL-based action buttons, debounce loops, workspace exclusion regexes, and external HTTPS asset hosting) that can directly enhance Deskchirp.
3. **Agent-specific requirements** (token/cost telemetry, provider/model disclosure, multi-agent session deduplication, and lifecycle state management) that have no equivalent in traditional VS Code extensions.

---

## 2. Primary Source Popularity & Maintenance Metrics

All metrics below were queried directly from the GitHub REST API and Visual Studio Marketplace Gallery API on **2026-10-31** (exact measured values, not estimates).

### 2.1 Visual Studio Code Presence Extensions

| Project & Marketplace ID | Primary Repository URL | License | Stars | Forks | Open Issues | Watchers | Last Push (UTC) | Marketplace Installs | Rating (Count) | Last Marketplace Update |
| :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- |
| **Discord Presence** (`icrawl.discord-vscode`) | [iCrawl/discord-vscode](https://github.com/iCrawl/discord-vscode) | MIT | 1,338 | 1,937 | 44 | 5 | 2026-03-27 | 2,648,117 | 4.35 / 5 (133) | 2025-04-12 |
| **Discord Rich Presence** (`LeonardSSH.vscord`) | [LeonardSSH/vscord](https://github.com/leonardssh/vscord) | MIT | 694 | 147 | 44 | 5 | 2026-10-26 | 846,011 | 4.52 / 5 (29) | 2026-10-26 |
| **Yet another Discord Presence** (`satoqz.yet-another-discord-presence`) | [Satoqz/vscode-discord](https://github.com/Satoqz/vscode-discord) *(Repo 404)* | MIT *(Marketplace)* | N/A | N/A | N/A | N/A | N/A | 73,872 | 4.89 / 5 (9) | 2023-07-20 |

### 2.2 Modern AI Agent Discord Presence Projects (Contextual Reference)

| Project | Primary Repository URL | License | Stars | Forks | Open Issues | Watchers | Last Push (UTC) | Architecture Type |
| :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- |
| **claude-rpc** | [rar-file/claude-rpc](https://github.com/rar-file/claude-rpc) | MIT | 32 | 3 | 6 | 0 | 2026-10-30 | Node.js daemon driven by Claude Code CLI hooks & OAuth |
| **Codex-Discord-Rich-Presence** | [xt0n1-t3ch/Codex-Discord-Rich-Presence](https://github.com/xt0n1-t3ch/Codex-Discord-Rich-Presence) | MIT | 26 | 7 | 1 | 0 | 2026-10-17 | Rust daemon (Ratatui TUI) reading local JSONL sessions |

---

## 3. Detailed Comparative Feature Matrix

Source analysis conducted on local checkouts of `iCrawl/discord-vscode` (commit `45162ba`) and `LeonardSSH/vscord` (commit `73f4e24`).

| Feature Dimension | iCrawl/discord-vscode | LeonardSSH/vscord |
| :--- | :--- | :--- |
| **Transport & Protocol** | Uses `@xhayper/discord-rpc` (`v1.2.1`) over local IPC | Uses `@xhayper/discord-rpc` (`v1.3.4`) over local IPC |
| **Application ID Strategy** | Hardcoded Client ID: `383226320970055681` | Multi-app dropdown + Custom: `Code` (`782685898163617802`), `VS Code` (`810516608442695700`), `VSCodium` (`1031067701474492496`), `Antigravity` (`1441771215290372156`), `Cursor` (`1376937466619232256`), or user-defined `vscord.app.id` |
| **Activity States** | Editing, Debugging, Idling | Editing, Viewing, Debugging, Idling, NotInFile |
| **File & Language Resolution** | Static map of 109 languages and 414 extensions in `languages.json`. Maps to uploaded asset names. | Static map in `data/languages.json` + runtime regex overrides (`vscord.behaviour.additionalFileMapping`). Resolves to external image URLs. |
| **Workspace & Path Modeling** | Single workspace name, folder name, or `Workspace - WorkspaceFolder` format. Strip `(Workspace)` suffix. | Resolves root folder, multi-root folder, relative file path (`workspace.asRelativePath`), and strips remote schemes (`vscode-remote` regex removing `[(SSH\|WSL):...]`). |
| **Line, Column & File Size** | Replaces `{current_line}`, `{current_column}`, `{total_lines}`, `{file_size}` (supports bytes, KB, MB, GB, TB). | Replaces `{current_line}`, `{current_column}`, `{line_count}`, `{file_size}` via `filesize` package (IEC/SI units, configurable rounding and spacers). |
| **Linter Problems & Diagnostics** | Diagnostic error count (`DiagnosticSeverity.Error`) replaced into `{current_errors}`. | Configurable counted severities (`error`, `warning`, `info`, `hint`) replacing `{problems_count}`, `{problems_pluralize}`, etc. |
| **Git Integration** | Queries `vscode.git` extension API; extracts `HEAD.name` and remote fetch URL; strips `git@` and credentials. | Queries `vscode.git` API; uses `git-url-parse` and `normalize-url` for full breakdown: `{git_owner}`, `{git_repo}`, `{git_branch}`, `{git_url}`, `{git_protocol}`, `{git_host}`. |
| **Action Buttons** | 1 button: `View Repository` with sanitized Git remote HTTPS URL. | Up to 2 buttons: Independently configurable for Button 1 and Button 2 across Active, Inactive, Idle, and Git states. Built-in URL validation (`validURL`). |
| **Elapsed Time Management** | Single `startTimestamp` set at initial session or preserved across state updates. Can be disabled via `discord.removeTimestamp`. | Configurable `status.showElapsedTime`, per-file reset toggle (`status.resetElapsedTimePerFile`), and reset-on-idle toggle (`status.idle.resetElapsedTime`). |
| **Idle Detection & Throttle** | Window blur listener (`window.onDidChangeWindowState`) with configurable timeout (`discord.idleTimeout`, default 0 = disabled). Document edits throttled to 2,000ms. | Window blur listener with 300s default timeout (`vscord.status.idle.timeout`), optional disconnect on idle (`vscord.status.idle.disconnectOnIdle`), and leading/trailing activity debouncing. |
| **Privacy & Sanitization** | Workspace regex exclude list (`discord.workspaceExcludePatterns`). Global toggles to omit details, lower details, timestamp, or remote repository. | Exclude lists for workspaces (`ignore.workspaces`), repositories (`ignore.repositories`), GitHub organizations (`ignore.organizations`), and Git hosts (`ignore.gitHosts`). Discord User Whitelist/Blacklist. |
| **Asset Image Strategy** | Discord Developer Portal App Assets (hardcoded asset keys uploaded to Discord client ID `383226320970055681`). | External HTTPS image URLs hosted on GitHub: `https://raw.githubusercontent.com/LeonardSSH/vscord/main/assets/icons/{lang}.png`. |

---

## 4. Deep-Dive: Transferability to Agentic Desktop Applications

### 4.1 What Requires In-Process Editor APIs (Non-Transferable)

The following features depend strictly on editor runtime internals and cannot be cleanly reproduced by an out-of-process companion (such as Deskchirp observing window handles or polling read-only APIs):

1. **Cursor Coordinates & Selection State (`line:column`):**
   - *Implementation in vscord:* Read synchronously via `window.activeTextEditor.selection.active`.
   - *Why non-transferable:* Requires direct memory access to the active text document buffer. Our foreground detector supplies app identity only. Cursor positions would require an explicit editor integration; UI scraping is outside the recommended design.
2. **Real-Time Linter Diagnostics & Syntax Errors:**
   - *Implementation in vscord:* Subscribes to `vscode.languages.onDidChangeDiagnostics` and counts active `Diagnostic` objects.
   - *Why non-transferable:* Diagnostics reside in the language server client memory within the editor host. An external companion would have to duplicate compilation/linting pipelines locally.
3. **Continuous Typing Keystroke Throttling:**
   - *Implementation in discord-vscode:* Subscribes to `workspace.onDidChangeTextDocument` with a 2,000ms throttle.
   - *Why non-transferable:* An external companion has no concept of typing churn; agent desktop apps operate in turn-based conversational phases rather than keystroke streams.

### 4.2 What Transfers Cleanly to Agentic Desktop Apps

The following mechanisms provide direct architectural inspiration for Deskchirp:

1. **Sanitized Git Remote Action Buttons:**
   - *Mechanism:* Both extensions parse Git remotes and strip private SSH syntax (`git@github.com:org/repo.git` $
ightarrow$ `https://github.com/org/repo`) and embedded HTTP authentication credentials (`https://user:token@host/repo` $
ightarrow$ `https://host/repo`).
   - *Application for Deskchirp:* When Deskchirp detects an active workspace Git repository via a local metadata adapter, it can offer an opt-in `View Repository` action button pointing to verified public HTTPS URLs.
2. **Granular Privacy & Ignore Rules:**
   - *Mechanism:* `vscord` provides distinct exclude lists:
     - `ignore.workspaces`: Array of glob/regex paths.
     - `ignore.repositories`: Exclude specific repository names.
     - `ignore.organizations`: Exclude internal enterprise GitHub organizations.
     - `ignore.gitHosts`: Exclude private enterprise GitLab/GitHub Enterprise domain names (e.g., `git.internal.company.com`).
   - *Application for Deskchirp:* This 4-tier filtering hierarchy should be adopted directly. If an observed project belongs to an ignored host or organization, Deskchirp should automatically suppress the project name and repository button, falling back to a sanitized generic status (e.g., `Using T3 Code`).
3. **Monotonic Elapsed Timestamps:**
   - *Mechanism:* Storing a fixed Unix epoch timestamp (`startTimestamp = Date.now()`) and passing it through consecutive activity updates lets Discord render the ticking clock client-side without requiring outbound IPC packets every second.
   - *Application for Deskchirp:* Deskchirp should maintain a stable `sessionStartTime` tied to the companion session or thread focus, avoiding clock resets when agents transition between thinking, tool execution, and waiting states.
4. **Debounced Lifecycle State Transitions:**
   - *Mechanism:* `vscord` uses a dedicated throttle class with configurable timeouts to coalesce rapid window blur/focus events.
   - *Application for Deskchirp:* Debouncing foreground focus shifts prevents Discord Rich Presence flickering when a user briefly switches between T3 Code, a browser, and terminal windows.

---

## 5. Specific Topic Deep-Dives

### 5.1 Logos & Asset Strategy

#### A. Discord Platform Capabilities & Limits
Discord Rich Presence supports two distinct mechanisms for delivering large and small images:
1. **Discord Developer Portal Uploaded Assets:**
   - Application maintainers upload 1024x1024 (or 512x512) static PNG/JPEG/WebP images in the Discord Developer Portal under **Rich Presence $
ightarrow$ Art Assets**.
   - Each asset is referenced by a string key (e.g., `t3code-logo`, `thinking`, `idle`).
   - *Platform Limit:* Discord imposes a hard cap of **300 uploaded assets** per application ID.
2. **External HTTPS Asset URLs:**
   - In modern Discord Gateway and RPC schemas, `large_image` and `small_image` can accept a fully qualified public HTTPS URL (e.g., `https://raw.githubusercontent.com/.../icon.png`).
   - Discord's backend media proxy fetches and caches the external image.
   - *Platform Limit:* Bypasses the 300-asset cap entirely. However, if the external host fails, rate-limits, or blocks Discord's user-agent, the client renders a blank asset.

#### B. Architectural Comparison: iCrawl vs. LeonardSSH

| Project | Approach | Asset Count | Pros | Cons |
| :--- | :--- | :--- | :--- | :--- |
| **iCrawl/discord-vscode** | Uploaded Developer Portal Assets | ~150 uploaded keys | Avoids a separate third-party image host; Discord still controls asset delivery and caching. | Reached capacity constraints; cannot add hundreds of community syntax icons without deleting others. |
| **LeonardSSH/vscord** | External HTTPS URLs on GitHub Raw CDN | Unlimited (all assets in Git) | Flexible; instantaneous asset updates via Git commits without touching Developer Portal. | Depends on GitHub raw availability; subject to CDN caching latency and potential raw domain throttling. |

#### C. Recommendation for Deskchirp
Because Deskchirp is designed for AI coding environments (T3 Code, Claude Desktop, Codex Desktop) rather than 500+ text syntax grammars, its asset requirements are compact:
- **Application Logos (Large Image):** 3 core application icons (T3 Code, Claude, Codex) plus build variants (Dev, Nightly, Alpha).
- **Activity Status Badges (Small Image):** 4 lifecycle badges (Working/Thinking, Needs Attention/Waiting, Paused, Idle).
- **Model Provider Marks (Optional Small Image):** ~6 provider icons (Anthropic, OpenAI, Google Gemini, Meta/Llama, DeepSeek, Local/Ollama).

**Total required assets: < 20.**
Therefore, Deskchirp should:
1. Register a dedicated Discord Application ID owned by the project.
2. Upload high-resolution (1024x1024 PNG) curated assets directly to the Developer Portal. This removes a third-party image-host dependency; rendering still needs validation on Discord clients.
3. Support an optional configuration override (`customAssetBaseUrl`) permitting advanced users or fork developers to provide external HTTPS URLs if they wish to customize icons.

---

### 5.2 Session Tokens and Cost Telemetry

#### A. Precedents in AI Presence Projects
Two active projects in the ecosystem implement token and cost telemetry:

1. **`rar-file/claude-rpc` (Claude Code):**
   - Captures prompt and response tokens from CLI execution hooks.
   - Exposes templates: `{tokensFmt} tokens`, `{tokensLabel}` (renders e.g., `14.2k tokens`).
   - Maintains a local SQLite/JSON history to render historical activity heatmaps and calculate daily token totals.
   - Reads the local Anthropic OAuth token to query `api.anthropic.com` for 5-hour session and weekly rate limit utilization percentages.
2. **`xt0n1-t3ch/Codex-Discord-Rich-Presence` (Codex CLI / VS Code):**
   - Parses local JSONL session logs (`~/.codex/sessions/*.jsonl`) and extracts input, output, and cache usage from supported local session formats.
   - Implements a dedicated pricing engine (`src/cost.rs`) that models non-cached input tokens, cached input tokens, cache writes, and output tokens against published model pricing schedules.
   - Renders priced dollar estimates (e.g., `$0.042`, `$1.28`) into the Discord activity `details` or `state` lines via a prioritized string packer (`compact_join`).

#### B. Tradeoffs, Privacy Risks, and Technical Constraints

| Dimension | Risk / Constraint | Recommended Mitigation |
| :--- | :--- | :--- |
| **Financial Privacy** | Broadcasting dollar expenditure on Discord profiles exposes commercial budgets, project scale, and personal spending habits to server members and mutual friends. | **Strictly opt-in, disabled by default.** Require explicit user activation in settings. |
| **Flat-Rate Subscription Distortion** | Many developers utilize fixed-cost subscriptions (Claude Pro/Max, ChatGPT Plus/Team, GitHub Copilot). An API-equivalent estimate does not establish an actual subscription charge. | Label dollar values as **`API Est.`** or **`Est. Value`**, never as actual charges. Provide a setting to display **Token Count only** (`842K tokens`) rather than currency. |
| **Discord Update Rate Limits** | Streaming LLM tokens change multiple times per second. Discord's local IPC socket drops or rate-limits `SET_ACTIVITY` calls if spammed faster than once every 2–5 seconds. | Token updates must be **coalesced and debounced** to a maximum cadence of once every 5–10 seconds. |
| **Character Budget in Discord UI** | Use a conservative 128-character cap for text fields and verify Unicode handling. Visible truncation varies with client, font, and width; there is no fixed 35–45-character API limit. | Place token and cost metrics either as a compact secondary token suffix (e.g., `T3 Code · 842K tok`) or within the **hover tooltip text** (`largeImageText`), keeping primary lines readable. |

---

### 5.3 Community T3 PR #18346 & Scope Analysis

#### A. PR Factual Summary
- **Pull Request:** [pingdotgg/t3code#18346](https://github.com/pingdotgg/t3code/pull/18346) — `feat(desktop): add opt-in Discord Rich Presence`
- **Author:** `snekxs`
- **Submission Date:** 2026-10-11
- **Current State:** OPEN in the GitHub API; maintainer comment requests closure pending scope approval in Discussion [#18348](https://github.com/pingdotgg/t3code/discussions/18348))
- **Scale:** +1,927 lines / -1 line across 24 files.

#### B. Code Architecture in PR #18346
PR #18346 proposed an in-app Electron integration for T3 Code:
1. **Type Contracts (`packages/contracts`):**
   - Extended `ClientSettingsSchema` with `discordRichPresenceEnabled: Schema.Boolean` (default `false`) and `discordPresenceFields: Schema.Array(Schema.Literal("project", "model", "activity", "elapsed"))`.
   - Extended `DesktopBridge` IPC with `setDiscordPresence`, `getDiscordPresenceStatus`, and `onDiscordPresenceStatus`.
2. **Main Process Effect Service (`apps/desktop`):**
   - Implemented an Effect-managed service (`DesktopDiscordPresence.ts`) using `Effect.fn` and single-permit `Semaphore` serialization.
   - Implemented zero-dependency IPC in `discordIpc.ts` using native Node.js `node:net` streams connecting to Unix domain sockets (`/tmp/discord-ipc-{0..9}`, `/run/user/1001`, Flatpak sandbox paths) and Windows named pipes (`\?\pipe\discord-ipc-{0..9}`).
3. **Renderer State Coordinator (`apps/web`):**
   - Created `DiscordPresenceCoordinator.tsx` mounted at the root router.
   - Aggregated active agent threads across **all connected environments** (local, remote SSH, Tailscale, T3 Connect) using `useEnvironmentIds()` and `environmentShell()`.
   - Separated active turns into `workingThreads` and `attentionThreads`, explicitly filtering out delegated subagent child threads (`delegate_task`).
4. **Branding Assets:**
   - Mapped internal build channels to Discord asset keys: Dev $
ightarrow$ `blueprint`, Nightly $
ightarrow$ `nightly`, Alpha $
ightarrow$ `black`.

#### C. Reviewer & Maintainer Reception
- **CodeRabbit AI:** Issued an approved review after its reported fixes. This is not maintainer approval or independent runtime verification.
- **Macroscope Bot:** Reported blocking correctness findings; see the separate T3 report for review details. Maintainer scope approval is a separate requirement.
- **Maintainer Response (Julius Marminge - `juliusmarminge`):**
  > *"This adds a new Discord integration that publishes project, model, and agent activity. The PR explicitly states that maintainer approval has not been obtained. The contribution guide requires approval of a new feature's direction and scope, so I'm closing this PR. Please propose the integration in an Ideas discussion and obtain explicit scope approval before requesting reconsideration."*
- **Subsequent Action:** Ideas Discussion [#18348](https://github.com/pingdotgg/t3code/discussions/18348) was initiated. Contributor `geekynasir` submitted a scoped compromise proposal emphasizing desktop-only operation, default-off settings, zero prompt/code leakage, and minimal maintainer burden.

#### D. Relationship Between PR #18346 and Deskchirp
PR #18346 is an **in-process Electron desktop feature** for T3 Code.
In contrast, **Deskchirp** is an **independent, multi-app companion** written in native Swift (macOS) and C#/WinUI 3 (Windows).
- *Decoupling Advantage:* Deskchirp does not require maintainer sign-off on T3 Code's core repository, does not bloat T3 Code's Electron bundle, and serves developers using Claude Desktop and Codex Desktop alongside T3 Code.
- *Integration Path:* Instead of embedding socket code into T3 Code, The proposed Deskchirp adapter would read T3 Code activity via T3's documented, authenticated, read-only MCP outside-agents interface.

---

## 6. Recommendations for Deskchirp

Based on verified ecosystem practices, the following architectural roadmap is recommended for `the Deskchirp checkout`:

### Phase 1: High-Value Quick Transfers (No Deep Editor Hooks Required)
1. **Git Remote & Public Action Button:**
   - Require a user-selected public repository URL or a validated metadata adapter first. Foreground app identity cannot identify the current workspace. Repository discovery belongs after that prerequisite.
   - Clean remote URLs (convert SSH/git@ syntax to HTTPS; strip HTTP user:token credentials).
   - Offer an opt-in Discord Action Button: `[View Repository]` pointing to the public URL.
2. **4-Tier Exclusion Hierarchy (Inspired by vscord):**
   - Add preferences for:
     - `ignoredWorkspaces` (glob/regex pattern list)
     - `ignoredRepositories` (exact repo names)
     - `ignoredOrganizations` (GitHub/GitLab org names)
     - `ignoredGitHosts` (enterprise host domain names)
   - When a match occurs, suppress project identity and repository buttons immediately.
3. **Monotonic Session Elapsed Clocks:**
   - Anchor `startTimestamp` to the active focus session epoch. Do not reset timestamps on transient window unfocus; provide a user toggle to reset only after a prolonged idle period (e.g., > 10 minutes).
4. **Curated Native Asset Uploads:**
   - Register a dedicated companion Discord Application ID.
   - Upload high-resolution 1024x1024 application marks (T3 Code, Claude, Codex) and small overlay badges (Working, Waiting, Idle).

### Phase 2: Agent Telemetry & Metadata Adapters (Via Authenticated MCP)
1. **T3 Code MCP Adapter:**
   - Connect to T3 Code via its documented read-only MCP interface (`outside-agents`).
   - Interpret the actual thread/run schema to derive confirmed working counts and only supported attention states without polling SQLite databases or inspecting Electron window titles.
   - Exclude internal subagents (`delegate_task`) to prevent artificial inflation of active tasks.
2. **Context & Token Reporting (Strictly Opt-In):**
   - Support displaying token totals (`842K tokens`) derived from adapter session summaries.
   - Avoid displaying synthetic dollar amounts by default. If cost estimates are enabled, explicitly mark them as `Est.` and provide user-configurable model rate tiers.
   - Pack token metrics into the image hover tooltip (`largeImageText`) to respect Discord's horizontal text truncation budget.

---

## 7. Primary Source URL Index

- **iCrawl/discord-vscode:**
  - Repository: [https://github.com/iCrawl/discord-vscode](https://github.com/iCrawl/discord-vscode)
  - Marketplace: [https://marketplace.visualstudio.com/items?itemName=icrawl.discord-vscode](https://marketplace.visualstudio.com/items?itemName=icrawl.discord-vscode)
- **LeonardSSH/vscord:**
  - Repository: [https://github.com/leonardssh/vscord](https://github.com/leonardssh/vscord)
  - Marketplace: [https://marketplace.visualstudio.com/items?itemName=LeonardSSH.vscord](https://marketplace.visualstudio.com/items?itemName=LeonardSSH.vscord)
- **rar-file/claude-rpc:**
  - Repository: [https://github.com/rar-file/claude-rpc](https://github.com/rar-file/claude-rpc)
- **xt0n1-t3ch/Codex-Discord-Rich-Presence:**
  - Repository: [https://github.com/xt0n1-t3ch/Codex-Discord-Rich-Presence](https://github.com/xt0n1-t3ch/Codex-Discord-Rich-Presence)
- **T3 Code PR #18346 & Discussion #18348:**
  - PR #18346: [https://github.com/pingdotgg/t3code/pull/18346](https://github.com/pingdotgg/t3code/pull/18346)
  - Maintainer Closure Comment: [https://github.com/pingdotgg/t3code/pull/18346#issuecomment-2405788448](https://github.com/pingdotgg/t3code/pull/18346#issuecomment-2405788448)
  - Discussion #18348: [https://github.com/pingdotgg/t3code/discussions/18348](https://github.com/pingdotgg/t3code/discussions/18348)
- **Discord Developer Documentation:**
  - Gateway Activity Object: [https://discord.com/developers/docs/events/gateway-events#activity-object](https://discord.com/developers/docs/events/gateway-events#activity-object)
  - RPC SET_ACTIVITY: [https://discord.com/developers/docs/topics/rpc#setactivity](https://discord.com/developers/docs/topics/rpc#setactivity)
  - Social SDK Rich Presence: [https://discord.com/developers/docs/discord-social-sdk/development-guides/setting-rich-presence](https://discord.com/developers/docs/discord-social-sdk/development-guides/setting-rich-presence)

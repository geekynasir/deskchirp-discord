# Claude and Codex: desktop telemetry feasibility

Reviewed 2026-10-31. This report distinguishes official protocol capabilities from demonstrated access to a running desktop application. It replaces the initial delegated draft after source review corrected several absolute claims.

## Findings

| Source | What is established | What is not established |
| --- | --- | --- |
| Native foreground detector | Which mapped application is frontmost | Whether an agent is running, its model, project, or token count |
| Codex app-server | Thread metadata and `thread/tokenUsage/updated` for accessible sessions | A supported discovery/observer connection to an arbitrary running Codex Desktop session |
| Claude Code status line | Model, context percentage, estimated session cost, optional limit fields | Access to Claude Desktop GUI chats or a cumulative token total from context fields |
| T3 thread-list MCP | Thread metadata for authorized environments | Usage/cost in that tool, or which thread the desktop is currently showing |
| T3 `server.getUsageSummary` RPC | Transcript-free provider/model/time-window aggregates in inspected source | Exact selected desktop session attribution or a stable third-party MCP contract |

Desktop focus presence remains the baseline for Claude Desktop and Codex Desktop. More telemetry is an adapter investigation, not a feature we can promise from process detection.

## Codex official protocol

Sources:
- [Official app-server documentation](https://developers.openai.com/codex/app-server/) (redirected to `https://learn.chatgpt.com/docs/app-server` during inspection).
- [OpenAI Codex source](https://github.com/openai/codex/tree/main/codex-rs/app-server).
- T3 generated protocol bindings under `packages/effect-codex-app-server/src/_generated/schema.gen.ts`.

The documented protocol includes stdio, experimental WebSocket support, and a Unix control socket. Therefore, a categorical claim that Codex has no local IPC interface is incorrect. The unresolved question is **whether a supported, authorized connection can discover and observe the user's already-running desktop session without resuming, starting, or otherwise changing its threads**. Launching a separate server does not by itself demonstrate that capability.

`thread/tokenUsage/updated` includes cumulative `tokenUsage.total`, recent-completion `tokenUsage.last`, and optional `modelContextWindow`. The total includes input/output categories; cached input and reasoning output are subsets of their respective totals. Do not add them again. Inspect each protocol version rather than assuming optional cache-write fields exist everywhere.

A context percentage derived from recent input divided by context capacity is an approximation tied to that engine's semantics, not cumulative processed usage. Prefer the engine's documented context indicator when available.

The protocol's token counts do not establish a dollar bill. Model-priced API equivalence can be computed only with a known model, applicable rates, cache categories, and explicit provenance. Organization API usage is also not a per-desktop-session observer.

Before implementing a desktop adapter, verify discovery, authentication, read-only behavior, thread identity, event delivery, coverage after desktop restarts, and ownership of historical versus live sessions. Do not start or resume a thread to watch it.

## Claude Code official status line

Primary source: [Customize your status line](https://code.claude.com/docs/en/statusline), inspected directly on 2026-10-31.

The current reference describes:
- `model.display_name`: model label.
- `context_window.used_percentage`: context occupancy.
- `context_window.total_input_tokens` and `total_output_tokens`: **counts currently in context from the most recent API response**, not cumulative session throughput. Input includes cache reads/writes.
- `context_window.current_usage`: per-component counts from the last API call. Repeated status-line refreshes are not distinct API completions.
- `cost.total_cost_usd`: estimated session cost computed client-side at list price unless a model-pricing table applies. It may differ from the bill and resets when `/clear` starts a new session.
- Optional `rate_limits.five_hour` and `seven_day` fields: quota utilization/reset time, a separate metric from context or tokens.

An opt-in helper could forward allowlisted fields from a user's **Claude Code** status-line invocation to the native companion. It should compose with an existing status line rather than silently overwrite it. This establishes no access to Claude Desktop's GUI conversations, including code-related workflows inside that application.

No supported general-purpose external observer for arbitrary Claude Desktop chats was established in the reviewed sources. That is a research limit, not proof that no possible integration can ever exist.

## Local transcript approaches

Community projects demonstrate JSONL parsing and hook-based adapters. Local files may help with cumulative usage and historical totals, but their format and desktop coverage need separate verification. Reading transcripts expands what the companion can access even if it publishes only numbers. Avoid private database scraping as a default. An optional future adapter would need explicit source selection, bounded reads, version handling, deduplication, and a clear local-data boundary.

The existing T3 usage service already performs provider transcript aggregation. If the user authorizes a T3 adapter, consuming its aggregate RPC may avoid duplicating that parser in our companion. Provider-wide totals must still not be labeled as activity from a particular desktop app.

## Cost and disclosure rules

- Label model-priced values `API est. ~$1.84`, never `Spent $1.84` without billing evidence.
- Subscription usage is not automatically incremental API spending. Extra usage, credits, or alternate account configurations also make a blanket `$0 per token for everyone` claim unsafe.
- Preserve provider-reported/model-priced/unpriced provenance. Provider-reported does not independently verify settlement or subscription billing.
- Unknown values remain unavailable; do not display a zero cost or fabricate a token total.
- Tokens, costs, quota percentages, projects, models, and background-work disclosure each need independent opt-in controls.

## Recommended sequencing

1. App artwork, badges, elapsed time, privacy controls, and trustworthy preview across all apps.
2. Authorized T3 thread metadata for workload/model/project features.
3. Evaluate T3 aggregate RPC for explicitly labeled provider/day totals.
4. Validate Codex Desktop attachment and Claude Desktop integration options independently. Offer Claude Code hooks as a clearly separate optional integration.

Current native binaries implement foreground presence only. These adapters are proposals.

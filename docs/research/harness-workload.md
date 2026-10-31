# Harness-aware T3 workload

Reviewed 2026-10-31 following the request to feature whichever harness is currently active or used most, including its subagent count. This extends the proposed companion roadmap; current binaries are unchanged.

## Feasibility

The existing thread-list MCP contract exposes `providerInstanceId`, `model`, `status`, `parentThreadId`, and `relationshipToParent`. It accepts `includeSubagents`; pass `true` explicitly and follow pagination. The inspected service currently includes subagents unless explicitly disabled.

`orchestrator_capabilities` resolves provider instance IDs to `driverKind`. That is the correct grouping key for harness families. Use public labels such as Claude Code, Codex, or Antigravity. A Claude model running through Antigravity belongs to Antigravity in harness mode; its model/provider may appear separately if enabled.

Custom instance display names can contain account, routing, or organization information. Do not publish them automatically. Use generic driver labels or an explicitly approved public alias.

Source references:
- [Thread and capability contracts](https://github.com/pingdotgg/t3code/blob/main/packages/contracts/src/orchestratorMcp.ts)
- [Thread-list implementation](https://github.com/pingdotgg/t3code/blob/main/apps/server/src/mcp/OrchestratorMcpService.ts)
- [Provider instance definitions](https://github.com/pingdotgg/t3code/blob/main/packages/contracts/src/providerInstance.ts)

## Counting rules

1. Include only authorized, selected environments/projects. Apply privacy exclusions before counting.
2. Deduplicate by environment and thread identity. A fork is a distinct task, not a subagent merely because it has a parent.
3. Classify `relationshipToParent === "subagent"` as a helper. Group it by its own harness, which can differ from the parent's.
4. Keep running, starting/queued, waiting, and completed categories separate. Completed children do not contribute to a live count. A waiting status alone does not prove a user approval is needed.
5. Show top-level tasks and helpers separately: `2 tasks · 4 subagents`. Their sum is the number of observed active execution contexts, not evidence of simultaneous API calls or CPU utilization.
6. Avoid counting both a native subagent record and its projected child thread as two workers. Prefer a linked child identity when available.
7. Disconnected environments lose live counts; partial coverage must be disclosed locally and qualified or omitted publicly. Unknown subagents are not zero.

## Which harness gets featured?

Offer three policies:

- **Most active now (recommended):** the harness with the most confirmed running workers in scope, including known subagents. Keep the current winner on ties and require a short sustained lead before changing it, to avoid rapid badge changes.
- **Pinned task:** follow a user-selected T3 task and its known family. An external companion cannot infer the currently viewed T3 tab from a thread list.
- **Most active this observed session:** optional later policy based on locally observed active worker-time. This is not historical token usage. Label it explicitly and do not claim pre-installation history.

Use the T3 logo as large artwork and the featured harness as the one small overlay. A status overlay is an alternative; Discord provides only one small image slot. The secondary line can summarize another active harness if it fits, with the full privacy-filtered breakdown in hover text.

Example text (fictional, with a constant registered companion title):

```
Deskchirp
T3 Code · Claude Code
2 tasks · 4 subagents
```

```
Deskchirp
T3 Code · Antigravity
6 active · Claude Code 2
```

## Native subagent boundary

T3's internal `OrchestrationV2Subagent` schema distinguishes `provider_native` and `app_owned`, with driver, instance, status, and optional child-thread/model fields. Internal records also contain prompts/results, so a presence adapter should not consume entire projections merely to count helpers.

The [Antigravity protocol adapter](https://github.com/pingdotgg/t3code/blob/main/apps/server/src/provider/acp/AntigravityProtocol.ts) explicitly notes that some subagent calls arrive as ordinary tools without child IDs or models. A `start_subagent`/`invoke_subagent` tool call is not automatically proof of one independently trackable live worker. The thread list is sufficient for known projected children and app-owned delegated tasks, but does not establish complete visibility into every harness-native child.

If exact native coverage is missing, expose only observed counts or propose a small metadata-only workload summary returning harness, stable worker identity, lineage, status, and coverage. Do not fetch prompts, code, tool outputs, or private session text to infer it.

## Recommendation

Make harness-aware workload a first-class T3 preset. Start with most-active-now, generic harness labels, and separate task/subagent counts for reliably exposed workers. Validate native-child coverage before presenting counts as exhaustive. This feature does not depend on token or cost telemetry.

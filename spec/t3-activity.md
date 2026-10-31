# T3 activity, version 2

Implementation contract for both native apps. Extends the existing foreground sharing gate. No upstream T3 changes, conversation reads, transcript parsing, external AI calls, or token/cost implementation.

## User experience

- Keep sharing OFF by default. Add a separate OFF-by-default **Show T3 activity** switch.
- Explain: “Show which agent tools are working and how many helpers they have.” Helpers are extra agents working on tasks.
- Connection section: **T3 Code**, **T3 address**, **Connect T3 Code**, **Disconnect**. Helper: “In T3 Code, open Settings → Environments and choose Copy MCP URL.” Keep MCP out of ordinary labels; the exact menu item is useful guidance.
- Open the user's browser for T3's own sign-in/approval. Request `orchestration:read` only, public OAuth PKCE S256, dynamic registration, localhost callback on an ephemeral loopback port. No token-paste field. Store credentials in macOS Keychain / Windows Credential Manager, never settings, logs, or presence.
- Connection status uses plain language: Not connected, Opening sign-in, Connected, Sign in again, Couldn't reach T3 Code. Add actionable recovery, not exception dumps.
- Most-active-now selects the agent tool with the most confirmed running tasks and helpers. Ties retain the current winner. A new winner needs a sustained 10-second lead unless the current winner has no running work.
- Discord details: `T3 Code · Claude Code` / `T3 Code · Antigravity`. State: `2 tasks · 4 helpers` with singular/plural forms. Count only `running` as running; waiting and starting/queued are separate. All-waiting can say `2 tasks waiting` / `1 helper waiting`; no false “needs attention” claim. Terminal rows do not count.
- Keep app-name-only presence when not opted in, unavailable, stale, unconnected, unknown, or no work. No invented zero counts.
- Other desktop apps retain app-name-only presence. T3 data never goes onto a Claude Desktop/Codex Desktop card.
- Show the complete outgoing details/state in native preview. Use generic public agent-tool names, never account/instance display names. No project, model, prompt, title, path, private URL, source fingerprint, or arbitrary server text in Discord.
- Optional OFF-by-default **Show app icons**: `assets.large_image = t3_code`, small image one of `harness_claude`, `harness_codex`, `harness_antigravity` for those known drivers; generic others omit small image. Help explains assets must be added to the configured Discord application; root supplies pack/setup guide. Do not imply remote asset keys magically exist.

## Data source

- One explicitly connected T3 environment in this version. HTTPS or HTTP loopback only; reject URL userinfo/query/fragment and wrong path. Normalize an origin or `/mcp` URL to its MCP endpoint. Don't silently follow HTTP redirects with credentials. Metadata/auth endpoints stay on the selected origin. Localhost means exact localhost/127.0.0.1/[::1], not suffix/string matches.
- Discover `/.well-known/oauth-protected-resource/mcp` and `/.well-known/oauth-authorization-server`. T3's endpoint paths are `/oauth/mcp/register`, `/oauth/mcp/authorize`, `/oauth/mcp/token`; use validated metadata.
- Register public client named Developer Presence with loopback callback URI, `token_endpoint_auth_method: none`, authorization_code grant. Authorize with response_type=code, client_id, redirect_uri, state (secure random), code_challenge/method S256, scope orchestration:read, resource exact MCP URL. Check state and issuer on callback, one-use code, bounded timeout/cancel. Token exchange uses form encoding and code_verifier; credential is Bearer, expires_in. No refresh token is promised by T3; expiry/401 asks to sign in again. Cancelled/failed sign-in must not overwrite a valid saved connection or keep a listening socket.
- MCP Streamable HTTP: initialize with a supported protocol version (2025-06-18), `notifications/initialized`, then `tools/call` for `orchestrator_capabilities` and `t3_thread_list`. Accept application/json and text/event-stream; preserve returned MCP session header and negotiated protocol header. Bound response length, timeouts, SSE accumulation, and validate matching JSON-RPC IDs/errors.
- Allowlist only these two tool calls. Use list arguments `allProjects:true, includeSubagents:true, limit:100`, paginate until nextCursor is null, cap pages/rows and reject incomplete snapshots rather than publishing partial totals as exhaustive. Root will verify exact schemas from the T3 checkout if needed.
- Decode only metadata fields: threadId, providerInstanceId, relationshipToParent, status. Ignore all titles, pull requests, links, and other server data. Catalog maps instance to driverKind; ignore displayName and models for this feature.
- Group by driver kind, never model prefix. Known safe names: claudeAgent → Claude Code; codex → Codex; antigravity → Antigravity; cursor → Cursor; opencode → OpenCode; grok → Grok; muse → Muse; pi → Pi. Unknown/custom driver maps to Other tools; never echo unknown strings.
- Deduplicate thread IDs within the connected environment. relationshipToParent=subagent means helper; fork remains a task. Count helpers under their own driver. T3-native helpers lacking projected child IDs are not inferred from tool names. Document observed coverage.
- Poll every 15 seconds while sharing/activity enabled, awake and T3 is foreground. Stop/cancel while disabled, paused, locked, sleeping, disconnected, or another app is foreground. No overlapping polls. Snapshot TTL 45 seconds, clear cached counts on failure/endpoint change/disconnect and never let a stale poll/auth callback restore counts after privacy changes. Foreground changes restart eligibility checks.

## Integration and tests

- Add injectable transport/source, clock and credential abstractions so core tests need no live server or Discord.
- Preserve old saved settings through default decoding of new fields (Swift synthesized Codable alone is insufficient). Don't break existing Discord/foreground privacy fixtures.
- Update Discord payload model/client to send a complete safe activity, not only SupportedApp. Keep ACK-based duplicate suppression and minimum 5-second updates; counts changing in the same app must trigger publication. Privacy clearing remains immediate. No compatibility method may bypass the allowlisted formatter.
- Meaningful tests: mixed-harness children; fork classification; completed/waiting filtering; duplicates/pagination; stable winner/ties; unknown labels/privacy stripping; malformed/SSE/error responses; URL/PKCE/state validation; stale updates/disconnect/privacy; old settings migration; same-app changed counts publish. Platform credential implementation and real sign-in must have honest validation limits.
- Use native platform controls, keyboard-accessible labels, wrapping helper text, system appearance. Keep technical terms in source/docs, not ordinary product copy.

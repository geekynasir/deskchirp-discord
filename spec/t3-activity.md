# T3 activity, version 2

Implementation contract for both native apps. Extends the existing foreground sharing gate. No upstream T3 changes, conversation reads, transcript parsing, external AI calls, or token/cost implementation.

## User experience

- Keep sharing OFF by default. Add a separate OFF-by-default **Show T3 activity** switch.
- Explain: “Show which agent tools are working and how many helpers they have.” Helpers are extra agents working on tasks.
- Connection section: **T3 Code**, **Allow T3 activity**, **Disconnect**. Discover local T3 when no address is configured; keep **T3 address** under **Advanced** for remote/custom environments. Never bypass read-only approval or open it repeatedly in the background. Successful explicit approval enables the activity toggle. Keep MCP out of ordinary labels; its exact Copy MCP URL menu name remains useful for the Advanced override.
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
- Register public client named Deskchirp with loopback callback URI, `token_endpoint_auth_method: none`, authorization_code grant. Authorize with response_type=code, client_id, redirect_uri, state (secure random), code_challenge/method S256, scope orchestration:read, resource exact MCP URL. Check state and issuer on callback, one-use code, bounded timeout/cancel. Token exchange uses form encoding and code_verifier; credential is Bearer, expires_in. No refresh token is promised by T3; expiry/401 asks to sign in again. Cancelled/failed sign-in must not overwrite a valid saved connection or keep a listening socket.
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

## Automatic setup

The publisher-owned Discord application ID is `1566210608569782373` (Deskchirp). New settings and existing blank-ID installs use it. Custom IDs and sharing/pause choices are preserved. Sharing stays off until the user enables it. Artwork stays off until publisher assets are ready.

Local T3 discovery is an explicit, cancellable part of Allow T3 activity. Read at most 16 KiB from `$T3CODE_HOME/userdata/server-runtime.json`, or `~/.t3/userdata/server-runtime.json`. Accept runtime version 1, a positive PID and port 1–65535; construct a loopback URL from the port, never follow stored origin URLs. Also check port 3773 for older/default installs. Probe only OAuth protected-resource metadata, without credentials; bound each request to two seconds and 16 KiB, refuse redirects, require resource/issuer alignment and orchestration:read. Zero matches shows recovery guidance; multiple matches requires an explicit address. Discovery does not read catalogs, logs, database rows, process arguments or tokens. Existing approved addresses reconnect using the existing secure-storage flow. Custom data paths and encrypted remote environment catalogs can require the Advanced override.

## Background T3 and bundled artwork (preview 3)

This update supersedes earlier focus-only and optional-upload rules. Poll while the mapped T3 desktop app is running, sharing/activity are enabled, the session is awake/unlocked and the linked-computer grant permits publication. T3 may be minimized. Fresh unfinished T3 work wins over another foreground app; otherwise prefer the foreground developer app, then app-only T3 while it remains running. On quit, invalidate T3 snapshots and stop polling. Never read process arguments or window titles to detect this. Recheck the selected app after Discord connection completes.

Artwork is enabled once on migration, with later opt-outs preserved. Resolve only the fixed asset whitelist to HTTPS URLs under the `artwork-v1` tag. No user path, project name or arbitrary URL may become an image URL. Discord fetches the public PNGs through its media proxy. The separate publisher application icon still requires authenticated publisher access.

## Following T3 Code’s connections (Windows, preview 5)

This supersedes “Discovery does not read catalogs” for this feature only; local discovery itself is unchanged. **Use T3 Code’s connections** defaults on and runs only while Show T3 activity is on. It keeps the environment list in step with the connections saved in the T3 Code desktop app.

- Read `$T3CODE_HOME/userdata/connection-catalog.json` (default `%USERPROFILE%\.t3`). Decrypt it as Electron safeStorage does: `v10` + 12-byte nonce + AES-256-GCM, keyed by the DPAPI-wrapped `os_crypt.encrypted_key` in the desktop app’s `Local State` (`t3code-v2`, `T3 Code (Alpha)`, `t3code`). Fall back to raw DPAPI. Zero plaintext and keys after parsing.
- Use only bearer routes with a saved credential (learned routes `learned:…@X` use X’s credential). Skip disabled environments, T3 Connect (`authorization: "t3-connect"`), relay, SSH and non-loopback plain HTTP; name skipped environments in a note. Validate every address with the normal T3 address rules.
- Send the desktop credential only to its route’s own origin, only to `POST /api/auth/pairing-token` for a one-time code (label Deskchirp, scope `orchestration:read`, plus `diagnostics:read` for usage). Never store, log or display it. Exchange the code with OAuth PKCE using `POST /oauth/mcp/decision` (`pairing-code`, `read-only`) and redirect `http://127.0.0.1/deskchirp`, which is never listened on; check redirect prefix, state, issuer and error before the token exchange.
- New environments are tagged with their T3 environment id and use T3’s label. An untagged environment with the same address is adopted, keeping its name and sign-in. Tagged environments missing from a successfully read catalog are removed with their sign-ins; a missing or undecryptable catalog removes nothing.
- Try routes in T3’s order, starting with the current address; move the entry when another route works. A signed-in entry whose polls fail with network errors tries only its other routes. Moving drops token access saved for the old address first. Failures wait 5 minutes unless the catalog changes. Recheck every 60 seconds, reading the catalog only when it changed, the last read failed, or work remains.
- Disconnect turns following off before removing sign-ins and waits for any in-flight pass. Recheck following and usage declines against current settings after every awaited mint.
- Environments the user removes stay out until following is turned off and on. Token usage pairs automatically only when the card style is Usage or usage is already allowed somewhere, and never again for an environment whose access the user removed.

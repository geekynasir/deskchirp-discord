> Development review: findings below describe the intermediate implementation inspected during development. See [verification status](../validation.md) for the final build and test results.

## Implementation follow-up

The native clients now initialize once per endpoint/sign-in, omit session headers on initialize, send both accepted response types on every POST, negotiate `2025-06-18`, and discard failed sessions. Both require a matching callback issuer. Windows emits string request IDs and validates their exact matches; numeric IDs cannot satisfy those requests. Mac requests use numeric IDs and validate those matches.

Both transports bound response bytes while receiving them and finish matching SSE replies without waiting for the stream to close. Malformed, incomplete, capped, failed, or expired data falls back to app-only presence. Regression tests cover these cases, late sign-in after disconnect, slow Windows polling without blocking focus changes, and actual Mac loopback callbacks. See [verification status](../validation.md) for the exact tested source and remaining real-machine checks. This is source/protocol validation against the upstream contract; no live user conversations were read.

# T3 Activity Interoperability Review: Native Developer Presence vs. Upstream T3 Code

**Document Version:** 1.0  
**Target Repository:** `/data/ubuntu/projects/developer-presence`  
**Upstream Repository:** `/data/ubuntu/projects/t3code-discord-presence`  
**Reviewed Specification:** `spec/t3-activity.md`  
**Review Scope:** OAuth discovery, dynamic registration, callback & PKCE handling, MCP initialization, Streamable HTTP JSON/SSE result shapes, `orchestrator_capabilities` and `driverKind`, `t3_thread_list` pagination, status, and lineage across macOS and Windows native implementations.

---

## 1. Executive Summary

This read-only interoperability review compares the native Developer Presence T3 activity specification (`spec/t3-activity.md`) and developing macOS/Windows client implementations against actual upstream schemas and implementations in the T3 Code monorepo (`@t3tools/monorepo`).

Both native implementations correctly implement the core privacy architecture: zero leakage of titles, paths, account display names, or model strings to Discord Rich Presence, strict loopback URL validation, 8-driver known safe mapping to generic tools, and winner selection with 10-second hysteresis.

However, the review uncovered **three critical blockers** (one in macOS, two in Windows) and **four targeted improvements** that must be resolved for reliable live interoperation with T3 Code's MCP and OAuth endpoints:

1. **[BLOCKER - macOS] MCP Session-Id Re-sent on `initialize`:** macOS `T3McpClient.swift` calls `initialize` on every 15-second poll while retaining `self.sessionId` from the prior poll. Upstream Effect MCP runtime (`mcpRuntime.ts:452`) strictly rejects any `initialize` request bearing an `mcp-session-id` header with HTTP 400 (`"initialize must not include an MCP-Session-Id header"`), breaking all subsequent polling after the initial cycle.
2. **[BLOCKER - Windows] Missing `text/event-stream` in Notification Accept Header:** Windows `T3McpClient.cs:316-318` sets `Accept: application/json` exclusively in `PostNotificationAsync`. Upstream Streamable HTTP transport (`McpServer.ts:1834`) enforces that the `Accept` header must contain *both* `application/json` and `text/event-stream`, returning HTTP 406 Not Acceptable and rejecting `notifications/initialized`.
3. **[BLOCKER - Windows] Stale Session & Initialization Lock on Disconnect/Reconnect:** Windows `T3McpClient.cs` caches `_initialized = true` and `_sessionId` permanently with no reset method. On user disconnect/reconnect or endpoint change, `EnsureInitializedAsync` skips initialization and sends invalid/expired session IDs to the target server, returning HTTP 404.
4. **[IMPROVEMENT - Spec & Upstream Alignment] Protocol Version Negotiation:** `spec/t3-activity.md` cites `2025-03-26`, but upstream T3 Code is built on `McpProtocol.v2025_06_18` (`2025-06-18`). Clients negotiating with `2025-03-26` receive `2025-06-18` and must strictly reflect `2025-06-18` in subsequent `MCP-Protocol-Version` headers, as `mcpRuntime.ts:251` returns HTTP 400 for unnegotiated version headers.
5. **[IMPROVEMENT - Cross-Platform] RFC 9207 `iss` Parameter Enforcement:** Upstream advertises `authorization_response_iss_parameter_supported: true` and sends `iss`. Both clients validate `iss` only when present rather than asserting its required presence.
6. **[IMPROVEMENT - Windows] SSE JSON-RPC ID Parsing:** Windows `ParseSseResponseAsync` uses `idProp.GetString() == expectedId`, which throws and swallows exceptions if a response uses integer IDs.
7. **[IMPROVEMENT - Windows] Non-Array JSON-RPC Response ID Validation:** Windows `PostJsonRpcAsync` verifies matching IDs only for JSON-RPC batch arrays, but passes through non-batch responses without checking `response.id == expectedId`.

---

## 2. Upstream Architecture & Specification Comparison

### Area 1: OAuth Discovery

#### Upstream Schema & Implementation
- **Endpoints Defined:** Mounted in `packages/contracts/src/environmentHttp.ts:606-626`:
  - `GET /.well-known/oauth-protected-resource/mcp` (`mcpProtectedResource`)
  - `GET /.well-known/oauth-protected-resource` (`protectedResource`, fallback)
  - `GET /.well-known/oauth-authorization-server` (`authorizationServer`)
- **Metadata Returned:** Handled by `apps/server/src/auth/McpOAuth.ts`:
  - `protectedResourceMetadata` (lines 60-70):
    ```ts
    {
      resource: "/mcp",
      authorization_servers: [""],
      scopes_supported: ["orchestration:read", "orchestration:operate"],
      bearer_methods_supported: ["header"],
      resource_name: "T3 Code"
    }
    ```
  - `authorizationServerMetadata` (lines 80-95):
    ```ts
    {
      issuer: "",
      authorization_endpoint: "/oauth/mcp/authorize",
      token_endpoint: "/oauth/mcp/token",
      registration_endpoint: "/oauth/mcp/register",
      response_types_supported: ["code"],
      grant_types_supported: ["authorization_code"],
      code_challenge_methods_supported: ["S256"],
      token_endpoint_auth_methods_supported: ["none"],
      scopes_supported: ["orchestration:read", "orchestration:operate"],
      authorization_response_iss_parameter_supported: true
    }
    ```
- **Unauthorized Challenge:** In `apps/server/src/mcp/McpHttpServer.ts:85-115`, unauthenticated or invalid credential requests return HTTP 401 with:
  `WWW-Authenticate: Bearer resource_metadata="/.well-known/oauth-protected-resource/mcp"`

#### Spec Evaluation
`spec/t3-activity.md` specifies discovering `/.well-known/oauth-protected-resource/mcp` and `/.well-known/oauth-authorization-server`, validating that metadata endpoints stay on the selected origin without userinfo/query/fragment. This matches upstream implementation and RFC 9728 / RFC 8414 exactly.

---

### Area 2: Dynamic Registration, Callback, State & Issuer

#### Upstream Schema & Implementation
- **Dynamic Registration Endpoint:** `POST /oauth/mcp/register` (`environmentHttp.ts:622`).
  - Input Schema: `AuthMcpClientRegistration` (`packages/contracts/src/auth.ts:454-460`):
    - `client_name`: optional string (truncated to 100 chars, defaults to `"MCP client"`).
    - `redirect_uris`: array of 1 to 5 URIs, max length 512, must be `https:` or loopback `http:` (`localhost`, `127.0.0.1`, `[::1]`).
    - `token_endpoint_auth_method`: optional string (must be `"none"`).
  - Output Schema: `AuthMcpRegisteredClient` (`packages/contracts/src/auth.ts:462-470`), returns HTTP 201 Created with signed `client_id` (`<base64url>.<hmac>`).
- **Authorization Request:** `GET /oauth/mcp/authorize` (`apps/server/src/auth/McpOAuth.ts:342-390`):
  - Parameters validated: `response_type=code`, `client_id`, `redirect_uri`, `code_challenge` (regex `^[A-Za-z0-9_-]{43}$`), `code_challenge_method=S256`, optional `state`, optional `resource` (must match `/mcp` if supplied).
  - Port-Agnostic Matching (`McpOAuth.ts:114-124`): For loopback redirects, port differences between registration and presentation are ignored per RFC 8252 §7.3.
- **Authorization Callback & RFC 9207 Issuer:**
  - On approval (`McpOAuth.ts:474-478`), the server redirects to `redirect_uri` with:
    `?code=<code>&state=<state>&iss=<issuer>`
  - On denial or error (`McpOAuth.ts:182-187`, `485-489`), the server includes `iss` in the redirect query.
- **Token Exchange:** `POST /oauth/mcp/token` (`apps/server/src/auth/McpOAuth.ts:490-560`):
  - Input format: Form URL-encoded (`HttpApiSchema.asFormUrlEncoded()`, `packages/contracts/src/auth.ts:549`).
  - Parameters: `grant_type=authorization_code`, `code`, `redirect_uri`, `client_id`, `code_verifier`, optional `resource`.
  - Output: `AuthMcpTokenResult` (`auth.ts:553-559`): `access_token`, `token_type: "Bearer"`, `expires_in`, `scope: "orchestration:read"`.

#### Spec Evaluation
`spec/t3-activity.md` requirements align with upstream: public client named `Developer Presence`, loopback callback on an ephemeral port, PKCE S256, verification of `state` and `iss`, and form URL-encoded token exchange.

---

### Area 3: MCP Initialization & Streamable HTTP Protocol

#### Upstream Schema & Implementation
- **Transport Endpoint:** `POST /mcp` configured in `apps/server/src/mcp/McpHttpServer.ts:1139-1145`.
- **Configured Protocols:**
  ```ts
  export const layerMcpTransport = McpServer.layerHttp({
    name: "T3 Code",
    version: packageJson.version,
    path: "/mcp",
    protocols: [McpProtocol.v2025_06_18],
    allowSessionTermination: true,
  });
  ```
  Upstream only supplies `v2025_06_18` (`2025-06-18`).
- **HTTP Header Contract:**
  1. `Content-Type: application/json` (checked in `McpServer.ts:1828`, fails with 415 if mismatched).
  2. `Accept`: Checked in `McpServer.ts:1834`:
     ```ts
     const accepted = mcpMediaTypes(request.headers["accept"])
     if (!accepted.includes("application/json") || !accepted.includes("text/event-stream")) {
       return Effect.succeed(HttpServerResponse.empty({ status: 406 }))
     }
     ```
     **Both `application/json` AND `text/event-stream` are required on every POST request.**
  3. `mcp-session-id`:
     - Must NOT be sent on `initialize` (`mcpRuntime.ts:452`, rejects with 400).
     - Emitted in HTTP response header `mcp-session-id: <uuid>` on `initialize` (`mcpRuntime.ts:575`).
     - Must be sent on all non-initialize requests (`mcpRuntime.ts:460`, rejects with 400 if missing).
  4. `mcp-protocol-version`:
     - Returned in response header on `initialize` as `2025-06-18`.
     - Validated in `mcpRuntime.ts:251`: if sent on subsequent calls, it MUST match a configured protocol (`2025-06-18`); unknown versions return HTTP 400.
- **Result Shapes & Encodings:**
  - Single JSON-RPC completion: returned as `application/json` HTTP 200.
  - Streaming / notifications: returned as `text/event-stream` HTTP 200 with `data: <json>

`.
  - Notifications (`notifications/initialized`): returned as HTTP 202 Accepted with an **empty response body** (`McpServer.ts:1862`, `McpHttpServer.ts:60-67`).
  - Tool calls: `CallToolResult` in `v2025_06_18` includes both:
    - `structuredContent`: the raw decoded typed result object.
    - `content`: array containing `[{ type: "text", text: JSON.stringify(result) }]`.

#### Spec Evaluation
`spec/t3-activity.md` mentions `initialize with a supported protocol version (2025-03-26)`. Upstream accepts `2025-03-26` during initialization fallback negotiation (`mcpRuntime.ts:210`), but negotiates up to `2025-06-18`. Clients must adopt the negotiated protocol version header.

---

### Area 4: `orchestrator_capabilities` & `driverKind`

#### Upstream Schema & Implementation
- **Tool Definition:** `OrchestratorCapabilitiesTool` in `apps/server/src/mcp/toolkits/orchestrator/tools.ts:72-83`.
  - Parameters: `OrchestratorMcpCapabilitiesInput` (`packages/contracts/src/orchestratorMcp.ts:572-580`), optional `environmentId` and optional `search`. Empty arguments `{}` are fully valid.
- **Output Schema:** `OrchestratorMcpCapabilitiesResult` (`orchestratorMcp.ts:631-650`):
  - `providers`: array of `OrchestratorMcpProviderCapability` (`orchestratorMcp.ts:589-605`):
    - `providerInstanceId`: `ProviderInstanceId` (string)
    - `driverKind`: `ProviderDriverKind` (slug string, `providerInstance.ts:15`)
    - `displayName`: `Schema.NullOr(Schema.String)` (private display name, must be ignored)
    - `models`: array of models (must be ignored)
- **Supported Driver Kinds:**
  Registered drivers in upstream codebase:
  - Claude Code: `claudeAgent` (`apps/server/src/provider/Drivers/ClaudeDriver.ts:108`)
  - Codex: `codex` (`apps/server/src/provider/Drivers/CodexDriver.ts:119`)
  - Antigravity: `antigravity` (`apps/server/src/provider/Drivers/AntigravityDriver.ts:92`)
  - Cursor: `cursor` (`packages/provider-cursor/src/client.ts:7`)
  - OpenCode: `opencode` (`packages/provider-opencode/src/client.ts:7`)
  - Grok: `grok` (`packages/provider-grok/src/client.ts:7`)
  - Muse: `muse` (`packages/provider-muse/src/client.ts:7`)
  - Pi: `pi` (`packages/provider-pi/src/client.ts:7`)
  - Internal/Other: `acpRegistry`, `fx`, etc.

#### Spec Evaluation
`spec/t3-activity.md` exactly lists the 8 safe public drivers and specifies that all other drivers (such as `acpRegistry` or custom drivers) must map to `"Other tools"` with no asset icons. Both client implementations adhere strictly to this rule.

---

### Area 5: `t3_thread_list` Pagination, Status, and Lineage

#### Upstream Schema & Implementation
- **Tool Definition:** `ThreadListTool` in `apps/server/src/mcp/toolkits/orchestrator/tools.ts:120-131`.
- **Input Parameters:** `OrchestratorMcpThreadListInput` (`packages/contracts/src/orchestratorMcp.ts:330-344`):
  - `allProjects`: optional boolean
  - `includeSubagents`: optional boolean
  - `limit`: optional integer (`<= 100`)
  - `cursor`: optional non-negative integer (`NonNegativeInt`, 0, 100, 200...)
  - `statuses`: optional array of status strings
- **Result Schema:** `OrchestratorMcpThreadListResult` (`packages/contracts/src/orchestratorMcp.ts:382-390`):
  - `threads`: array of `OrchestratorMcpThreadListItem` (`orchestratorMcp.ts:355-380`):
    - `threadId`: string
    - `providerInstanceId`: string
    - `status`: `OrchestratorMcpThreadStatus` (`orchestratorMcp.ts:306`), resolved in `OrchestratorMcpService.ts:766` as `shell.activityRunStatus ?? shell.status`
    - `relationshipToParent`: `Schema.NullOr(Schema.Literals(["fork", "subagent"]))`
    - `parentThreadId`: `Schema.NullOr(ThreadId)`
  - `nextCursor`: `Schema.NullOr(NonNegativeInt)` (integer offset or `null` when exhausted)
  - `total`: integer total count matching filter
- **Status Value Space:**
  `OrchestratorMcpThreadStatus` union of `"idle"` and `OrchestrationV2RunStatus` (`packages/contracts/src/orchestrationV2.ts:580-592`):
  `"idle" | "preparing" | "queued" | "starting" | "running" | "waiting" | "completed" | "interrupted" | "failed" | "cancelled" | "rolled_back"`

#### Spec Evaluation
`spec/t3-activity.md` specifies:
- Counting only `"running"` as running.
- Separating `"waiting"` and `"starting"/"queued"/"preparing"`.
- Ignoring terminal (`completed`, `failed`, `cancelled`, `interrupted`, `rolled_back`) and `idle` rows.
- `relationshipToParent == "subagent"` classifies as a helper; `null` or `"fork"` classifies as a task.
- Deduplicating thread IDs and capping at 10 pages / 1000 rows.

---

## 3. Platform Client Findings: macOS Client

### Summary of Source Code Inspected
- `macos/Sources/DeveloperPresenceCore/T3/T3McpClient.swift`
- `macos/Sources/DeveloperPresenceCore/T3/T3OAuthClient.swift`
- `macos/Sources/DeveloperPresenceCore/T3/T3ActivityService.swift`
- `macos/Sources/DeveloperPresenceCore/T3/T3UrlValidator.swift`
- `macos/Sources/DeveloperPresenceCore/T3/T3WinnerSelector.swift`
- `macos/Sources/DeveloperPresenceCore/T3/OAuthLoopbackServer.swift`
- `macos/Sources/DeveloperPresenceCore/Models/T3Models.swift`
- `macos/Sources/DeveloperPresenceCore/Privacy/T3ActivityFormatter.swift`

---

### macOS Issues

#### [BLOCKER] MCP Session-Id Re-sent on `initialize` on Subsequent Polls
- **Primary Source Reference:**
  - Client: `macos/Sources/DeveloperPresenceCore/T3/T3McpClient.swift:70-76` and `236-238`
  - Upstream Server: `node_modules/.pnpm/effect@4.0.5/node_modules/effect/src/ai/internal/mcpRuntime.ts:452-456`
- **Details:**
  In `T3McpClient.swift`, `poll()` executes:
  ```swift
  public func poll(endpoint: T3NormalizedEndpoint, token: String) async throws -> ... {
      try await initialize(endpoint: endpoint, token: token)
      try await sendInitializedNotification(endpoint: endpoint, token: token)
      ...
  }
  ```
  In `postJsonRpc`, headers are attached:
  ```swift
  lock.lock()
  if let sId = sessionId {
      req.setValue(sId, forHTTPHeaderField: "mcp-session-id")
  }
  ...
  lock.unlock()
  ```
  When `poll()` runs the first time, `sessionId` is `nil`, so `initialize` succeeds and sets `self.sessionId = newSessionId`.
  When the 15-second polling timer triggers the second poll, `poll()` calls `initialize` again, but this time `self.sessionId` is populated and attached to the request headers.
  Upstream `mcpRuntime.ts` contains an explicit guard:
  ```ts
  const isInitialize = Predicate.hasProperty(input, "method") && input.method === "initialize"
  const hasSession = headers[MCP_SESSION_ID_HEADER] !== undefined
  if (isInitialize && hasSession) {
    return reject(
      400,
      new PublicMcpSchema.InvalidRequest({ message: "initialize must not include an MCP-Session-Id header" }),
      id
    )
  }
  ```
  The T3 Code server rejects the request with HTTP 400. Polling fails completely after the first cycle.
- **Actionable Fix:**
  `T3McpClient.swift` should maintain session state across polls. Check if a session already exists; only call `initialize` and `notifications/initialized` if `sessionId == nil` or if a subsequent call fails with HTTP 404 (`session not found`). Alternatively, if re-initializing each poll cycle, explicitly clear `sessionId = nil` before sending `initialize`.

---

#### [IMPROVEMENT] RFC 9207 `iss` Parameter Enforcement on Callback
- **Primary Source Reference:**
  - Client: `macos/Sources/DeveloperPresenceCore/T3/OAuthLoopbackServer.swift:341-350`
  - Upstream Server: `apps/server/src/auth/McpOAuth.ts:92`
- **Details:**
  Upstream advertises `authorization_response_iss_parameter_supported: true`. Under RFC 9207 §2.3, clients MUST require the presence of `iss` when the server advertises support. The macOS loopback server only checks `iss` conditionally if present (`if let iss = queryMap["iss"], !iss.isEmpty`).
- **Actionable Fix:**
  Require `guard let iss = queryMap["iss"], !iss.isEmpty` when the discovered metadata has `authorization_response_iss_parameter_supported == true`.

---

#### [IMPROVEMENT] MCP Client Session Reset on Polling Error
- **Primary Source Reference:**
  - Client: `macos/Sources/DeveloperPresenceCore/T3/T3ActivityService.swift:270-320`
- **Details:**
  When a polling cycle catches an error (e.g. 401 Unauthorized or server disconnection), `mcpClient.resetSession()` is not invoked; it is only called on explicit `disconnect()`. If the server restarts and invalidates the session, `T3McpClient` retains the stale session ID.
- **Actionable Fix:**
  Call `mcpClient.resetSession()` when handling connection or authentication failures in `T3ActivityService.pollOnce()`.

---

## 4. Platform Client Findings: Windows Client

### Summary of Source Code Inspected
- `windows/src/DeveloperPresence.Core/T3/T3McpClient.cs`
- `windows/src/DeveloperPresence.Core/T3/IT3McpClient.cs`
- `windows/src/DeveloperPresence.Core/T3/T3OAuthClient.cs`
- `windows/src/DeveloperPresence.Core/T3/T3ActivityService.cs`
- `windows/src/DeveloperPresence.Core/T3/T3McpModels.cs`
- `windows/src/DeveloperPresence.Core/T3/T3OAuthModels.cs`
- `windows/src/DeveloperPresence.Core/T3/T3WorkloadAggregator.cs`
- `windows/src/DeveloperPresence.Core/T3/T3LeaderTracker.cs`

---

### Windows Issues

#### [BLOCKER] Missing `text/event-stream` in Accept Header for Notifications
- **Primary Source Reference:**
  - Client: `windows/src/DeveloperPresence.Core/T3/T3McpClient.cs:314-325`
  - Upstream Server: `apps/server/src/mcp/McpHttpServer.ts` / `node_modules/.../effect/src/ai/McpServer.ts:1834-1836`
- **Details:**
  In `T3McpClient.cs`, `PostNotificationAsync` sets:
  ```csharp
  request.Headers.Accept.Clear();
  request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
  ```
  However, upstream `McpServer.ts` enforces:
  ```ts
  const accepted = mcpMediaTypes(request.headers["accept"])
  if (!accepted.includes("application/json") || !accepted.includes("text/event-stream")) {
    return Effect.succeed(HttpServerResponse.empty({ status: 406 }))
  }
  ```
  Because `text/event-stream` is missing, T3 Code immediately rejects `notifications/initialized` with HTTP 406 Not Acceptable.
- **Actionable Fix:**
  In `T3McpClient.cs:PostNotificationAsync`, add `text/event-stream` to the `Accept` header alongside `application/json`:
  ```csharp
  request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
  ```

---

#### [BLOCKER] No Session Reset on Disconnect/Reconnect in `T3McpClient`
- **Primary Source Reference:**
  - Client: `windows/src/DeveloperPresence.Core/T3/T3ActivityService.cs:205-230` and `T3McpClient.cs:96-105`
- **Details:**
  In `T3McpClient.cs`, `_initialized` is set to `true` once `EnsureInitializedAsync` runs, and `_sessionId` is permanently cached in the instance.
  `IT3McpClient` and `T3McpClient` have no `ResetSession()` method.
  When the user disconnects via `T3ActivityService.DisconnectAsync()` and connects to a different T3 Code instance, or when credentials expire, `_mcpClient` is never reset. On the next `PollOnceAsync`, `EnsureInitializedAsync` returns immediately because `_initialized == true`, and tool calls are dispatched with the stale session ID to the new endpoint, causing HTTP 404 or 401 errors.
- **Actionable Fix:**
  Add a `ResetSession()` method to `IT3McpClient` and `T3McpClient.cs` that resets `_initialized = false`, `_sessionId = null`, and `_protocolVersion = "2025-03-26"`. Invoke `_mcpClient.ResetSession()` inside `T3ActivityService.DisconnectAsync()` and on authentication expiry.

---

#### [IMPROVEMENT] JSON-RPC ID Parsing in SSE Stream
- **Primary Source Reference:**
  - Client: `windows/src/DeveloperPresence.Core/T3/T3McpClient.cs:282`
- **Details:**
  In `ParseSseResponseAsync`:
  ```csharp
  if (doc.RootElement.TryGetProperty("id", out var idProp) && idProp.GetString() == expectedId)
  ```
  If a server returns a response with a numeric ID (`ValueKind == JsonValueKind.Number`), `idProp.GetString()` throws an `InvalidOperationException`. This is swallowed by the surrounding `catch { }`, causing valid SSE messages to be discarded.
- **Actionable Fix:**
  Compare IDs safely across types:
  ```csharp
  string? actualId = idProp.ValueKind switch
  {
      JsonValueKind.String => idProp.GetString(),
      JsonValueKind.Number => idProp.GetInt64().ToString(),
      _ => null
  };
  if (actualId == expectedId) return doc;
  ```

---

#### [IMPROVEMENT] Response ID Validation for Non-Batch JSON Responses
- **Primary Source Reference:**
  - Client: `windows/src/DeveloperPresence.Core/T3/T3McpClient.cs:238-248`
- **Details:**
  In `PostJsonRpcAsync`, matching ID verification is performed if `doc.RootElement.ValueKind == JsonValueKind.Array` (batch), but if the response is a single object, it is returned without checking that `response.id == expectedId`.
- **Actionable Fix:**
  Validate `idProp` against `expectedId` on non-array root responses before returning.

---

#### [IMPROVEMENT] Enforce `iss` Parameter in Authorization Callback
- **Primary Source Reference:**
  - Client: `windows/src/DeveloperPresence.Core/T3/T3OAuthClient.cs:124-135`
- **Details:**
  Windows validates issuer only `if (!string.IsNullOrEmpty(callbackRequest.Issuer))`. When the authorization server metadata advertises `authorization_response_iss_parameter_supported: true`, the client should fail if `iss` is omitted.
- **Actionable Fix:**
  Enforce that `callbackRequest.Issuer` is non-empty and matches the normalized authority when supported by the server.

---

## 5. Summary Matrix: Blockers vs. Improvements

| ID | Platform | Component | Severity | Description | Upstream Reference | Client Reference |
|---|---|---|---|---|---|---|
| **MC-01** | macOS | `T3McpClient` | **BLOCKER** | `mcp-session-id` header sent on `initialize` on 2nd+ poll, returning HTTP 400 | `mcpRuntime.ts:452` | `T3McpClient.swift:70, 236` |
| **WN-01** | Windows | `T3McpClient` | **BLOCKER** | Accept header in `PostNotificationAsync` lacks `text/event-stream`, returning HTTP 406 | `McpServer.ts:1834` | `T3McpClient.cs:316` |
| **WN-02** | Windows | `T3McpClient` | **BLOCKER** | No session reset on disconnect/reconnect causes stale session reuse and HTTP 404 | `mcpRuntime.ts:460` | `T3ActivityService.cs:208`, `T3McpClient.cs:96` |
| **SP-01** | Both | Protocol Version | Improvement | Spec specifies `2025-03-26`, but upstream negotiates `2025-06-18` and enforces it | `McpHttpServer.ts:1142`, `mcpRuntime.ts:251` | `spec/t3-activity.md:34` |
| **MC-02** | macOS | `OAuthLoopback` | Improvement | Enforce `iss` presence when server advertises RFC 9207 support | `McpOAuth.ts:92` | `OAuthLoopbackServer.swift:341` |
| **MC-03** | macOS | `T3ActivityService` | Improvement | Clear MCP session on polling error / 401 | `McpHttpServer.ts:85` | `T3ActivityService.swift:270` |
| **WN-03** | Windows | `T3McpClient` | Improvement | Handle numeric JSON-RPC IDs in SSE stream parser without throwing | JSON-RPC 2.0 §4.4 | `T3McpClient.cs:282` |
| **WN-04** | Windows | `T3McpClient` | Improvement | Validate matching response ID on non-array JSON responses | MCP Streamable HTTP Spec | `T3McpClient.cs:248` |
| **WN-05** | Windows | `T3OAuthClient` | Improvement | Enforce `iss` presence when server advertises RFC 9207 support | `McpOAuth.ts:92` | `T3OAuthClient.cs:124` |

---

## 6. Actionable Recommendations for Platform Agents

### For macOS Agent (`macos/`):
1. **Fix Session Header on Initialize (`T3McpClient.swift`):**
   - Modify `T3McpClient.poll()` so that `initialize()` and `sendInitializedNotification()` are only called when `self.sessionId == nil`.
   - In `postJsonRpc`, ensure that `req.setValue(sId, forHTTPHeaderField: "mcp-session-id")` is skipped if `method == "initialize"`.
   - On HTTP 404 or session expiration from any tool call, set `self.sessionId = nil` and re-initialize.
2. **Enforce RFC 9207 `iss` Check (`OAuthLoopbackServer.swift`):**
   - In `waitForCallback`, verify `guard let iss = queryMap["iss"], !iss.isEmpty` and check that `iss.lowercased() == expectedIssuer.lowercased()`.

### For Windows Agent (`windows/`):
1. **Fix Notification Accept Header (`T3McpClient.cs`):**
   - In `PostNotificationAsync`, add `request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));`.
2. **Add Session Reset Capability (`IT3McpClient.cs` & `T3McpClient.cs`):**
   - Add `void ResetSession();` to `IT3McpClient`.
   - Implement `ResetSession()` to set `_initialized = false;`, `_sessionId = null;`, and `_protocolVersion = "2025-03-26";`.
   - Call `_mcpClient.ResetSession()` in `T3ActivityService.DisconnectAsync()` and `PollOnceAsync()` upon catching `T3AuthenticationException`.
3. **Robust SSE ID Parser (`T3McpClient.cs`):**
   - Inspect `idProp.ValueKind` and format as string before comparing with `expectedId`.

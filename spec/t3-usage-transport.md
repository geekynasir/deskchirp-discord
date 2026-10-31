# T3 usage transport

How Deskchirp reads token totals, context percentage, and estimated cost from an unmodified T3 Code server. Verified against a throwaway server built from T3 `da04ebc5` on 2026-10-31 unless marked *source only*.

The MCP credential cannot be reused: T3 refuses MCP sessions everywhere except `/mcp`.

## Pairing

1. On the computer running that T3 environment, the user runs:
   `t3 pair --scope orchestration:read --scope diagnostics:read --label Deskchirp`
   It prints a short code and a pairing URL `http(s)://host:port/pair#token=CODE`. Codes expire after 5 minutes and work once.
2. The user pastes either value into Deskchirp. Accept a bare code (`[A-Za-z0-9]{6,64}`) or a URL whose origin equals the environment's origin and whose fragment is `token=CODE`. Never accept a URL on another origin.
3. Exchange it (form encoded, no redirects, 10-second timeout, 64 KiB response cap):

```
POST {origin}/oauth/token
grant_type=urn:ietf:params:oauth:grant-type:token-exchange
subject_token=CODE
subject_token_type=urn:t3:params:oauth:token-type:environment-bootstrap
requested_token_type=urn:ietf:params:oauth:token-type:access_token
scope=orchestration:read diagnostics:read
client_label=Deskchirp
client_device_type=desktop
client_os=macOS|Windows
```

   200 → `{ access_token, token_type: "Bearer", expires_in, scope }`. The lifetime is 30 days with no refresh. Require `scope` to contain `diagnostics:read`, because T3 drops scopes the code did not grant instead of failing. 401 → `That code didn't work. Codes work once and expire after 5 minutes.`
4. Store `{ accessToken, expiresAt }` in secure storage under `deskchirp-usage:{origin}`. When the token expires or returns 401, show `Pair again to keep showing tokens` and delete it.
5. Do not send a DPoP header. A DPoP proof would cut the lifetime to one hour.

## WebSocket

- `GET {ws origin}/ws?orchestrationProtocol=2&clientSurface=deskchirp&clientAppVersion={version}`, with `Authorization: Bearer {token}`. Use no subprotocol. HTTP 426 means an incompatible T3 version: show `This T3 version isn't supported for token usage.`
- Before connecting, `GET /.well-known/t3/environment` and require `orchestrationProtocolVersion == 2`.
- Open one socket per usage poll, then close it after the answers arrive. A poll is bounded to 20 seconds, and each message is capped at 2 MiB.
- Frames are JSON text messages, each holding one object or an array of objects. Only the shapes below are used. Request ids are increasing decimal strings, and `headers` is always `[]`.

```
→ {"_tag":"Request","id":"1","tag":"server.getUsageSummary","headers":[],"payload":{"sinceDay":"2026-11-01","untilDay":"2026-11-01","timeZone":"Asia/Karachi","awaitRefresh":true}}
← {"_tag":"Exit","requestId":"1","exit":{"_tag":"Success","value":{…}}}
← {"_tag":"Exit","requestId":"1","exit":{"_tag":"Failure","cause":[{"_tag":"Fail","error":{"_tag":"EnvironmentAuthorizationError","requiredPermission":"diagnostics:read"}}]}}
→ {"_tag":"Request","id":"2","tag":"orchestration.getThreadProjection","headers":[],"payload":{"threadId":"…"}}
→ {"_tag":"Ping"}   ← {"_tag":"Pong"}
```

- Answer any `Chunk` with `{"_tag":"Ack","requestId":…}`. This version uses no streams, so a Chunk is unexpected and ends the poll.
- A `Failure` or `Die` makes that value unavailable. Never surface server text.

## Today (provider aggregate)

- `sinceDay = untilDay =` the local date in the computer's IANA time zone. Send the IANA identifier (Windows: convert with `TimeZoneInfo.TryConvertWindowsIdToIanaId`), never an offset.
- Require `contractVersion == 6`. Skip buckets that don't decode. Sum `totals` over all buckets: input = uncached + cached + cache creation, output = outputTokens (reasoning is a subset), cost = Σ `costUsd`.
- The value is **partial** when any source has a status other than `ok`, any bucket has `unpricedRecords > 0` (for cost only), or `pricing.status` is not `fresh`/`cached` (for cost only). Partial token data is not shown. Partial cost data hides the cost suffix only.
- Sum across all environments with usage permission. If any eligible environment fails, the combined value is partial.
- This counts the provider transcripts on that computer, including use outside T3. The hover text says so.

## Pinned task and session (thread projections)

`orchestration.getThreadProjection {threadId}` returns the thread's `providerThreads[]` and `providerTurns[]`. Decode only:

- `activeProviderThreadId`
- `providerThreads[].id`, `.contextUsage.{usedTokens,maxTokens}`
- `providerTurns[].{providerThreadId,startedAt,tokenUsage.{usedTokens,maxTokens,updatedAt},turnTokenUsage}`

Ignore every other key, including the timeline. The response carries conversation text, so never log or store it, and drop the raw bytes once decoded.

- **Turn tokens.** For `turnTokenUsage` with `usageStatus == "complete"`: input = `inputTokens` (already includes cache reads and writes; uncached = input − cached − cache creation, floored at 0), output = `outputTokens`. Any `partial`/`unavailable` turn in scope makes the total partial.
- **Pinned task tokens.** Sum the turns of the pinned thread.
- **Session tokens.** Sum the turns with `startedAt ≥ work-session start` across the running, waiting, and starting top-level threads and their helpers. Exclusions apply first. Request at most 24 projections per poll; if more are eligible, the value is partial.
- **Context.** Use the pinned thread's newest `providerTurns[].tokenUsage` by `updatedAt` on the active provider thread. Fall back to that provider thread's `contextUsage`. `maxTokens` missing, null, or 0 → unavailable.
- *Source only:* non-null `contextUsage`/`tokenUsage` values, and whether the ~1 MiB projection window ever trims `providerTurns`. Live validation needs a thread that has run a provider turn.

## Permission wording

Settings explain the permission honestly:
`Token usage needs a second permission from T3. It also allows reading diagnostics and task details. Deskchirp uses it only for token counts and never stores or sends conversation content.`

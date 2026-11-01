import Foundation
import CoreFoundation

// MARK: - Pairing

/// Pairing for the token usage permission (spec/t3-usage-transport.md, "Pairing").
public enum T3UsagePairing {
    public static let command = "t3 pair --scope orchestration:read --scope diagnostics:read --label Deskchirp"
    public static let explanation = "T3 needs a one-time pairing code from that computer. Run the command below there, then paste the code."
    public static let permissionWording = "Token usage needs a second permission from T3. It also allows reading diagnostics and task details. Deskchirp uses it only for token counts and never stores or sends conversation content."
    public static let keychainService = KeychainService.defaultService

    /// Secure-storage account: `deskchirp-usage:{normalized origin}`.
    public static func keychainAccount(origin: String) -> String {
        "deskchirp-usage:\(origin)"
    }

    /// Accepts a bare code (`[A-Za-z0-9]{6,64}`) or a pairing URL on the environment's own origin
    /// whose fragment is `token=CODE`. URLs on any other origin are refused.
    public static func parseCode(_ input: String, environmentOrigin: String) throws -> String {
        let trimmed = input.trimmingCharacters(in: .whitespacesAndNewlines)
        if isValidCode(trimmed) { return trimmed }
        guard let components = URLComponents(string: trimmed),
              let scheme = components.scheme?.lowercased(), scheme == "http" || scheme == "https",
              components.host != nil, components.user == nil, components.password == nil,
              components.query == nil,
              let url = components.url, url.isSameOrigin(as: environmentOrigin),
              let fragment = components.fragment, fragment.hasPrefix("token=") else {
            throw T3UsageError.invalidPairingCode
        }
        let code = String(fragment.dropFirst("token=".count))
        guard isValidCode(code) else { throw T3UsageError.invalidPairingCode }
        return code
    }

    static func isValidCode(_ value: String) -> Bool {
        (6...64).contains(value.count) && value.unicodeScalars.allSatisfy { scalar in
            switch scalar.value {
            case 0x30...0x39, 0x41...0x5A, 0x61...0x7A: return true
            default: return false
            }
        }
    }
}

// MARK: - Plans and results

/// What one environment must answer during a usage poll.
public struct T3UsagePollPlan: Equatable, Sendable {
    public var needsToday: Bool
    /// Thread IDs in this environment counted for `this session` (already capped and exclusion-filtered).
    public var sessionThreadIds: [String]
    /// True when more threads were eligible than the per-poll budget allowed.
    public var sessionOverflow: Bool
    public var sessionStart: Date?
    /// Thread ID of the pinned task when it lives in this environment.
    public var pinnedThreadId: String?

    public init(
        needsToday: Bool = false,
        sessionThreadIds: [String] = [],
        sessionOverflow: Bool = false,
        sessionStart: Date? = nil,
        pinnedThreadId: String? = nil
    ) {
        self.needsToday = needsToday
        self.sessionThreadIds = sessionThreadIds
        self.sessionOverflow = sessionOverflow
        self.sessionStart = sessionStart
        self.pinnedThreadId = pinnedThreadId
    }

    public var needsSession: Bool { !sessionThreadIds.isEmpty || sessionOverflow }
    public var isEmpty: Bool { !needsToday && !needsSession && pinnedThreadId == nil }
}

/// Per-environment answers. A nil value means unavailable, never zero.
public struct T3EnvironmentUsageResult: Equatable, Sendable {
    public var today: T3UsageValue?
    public var session: T3UsageValue?
    public var pinnedTokens: T3UsageValue?
    public var pinnedContext: T3UsageValue?

    public init(today: T3UsageValue? = nil, session: T3UsageValue? = nil,
                pinnedTokens: T3UsageValue? = nil, pinnedContext: T3UsageValue? = nil) {
        self.today = today
        self.session = session
        self.pinnedTokens = pinnedTokens
        self.pinnedContext = pinnedContext
    }
}

// MARK: - Decoding (pure)

public enum T3UsageDecoding {
    public static let supportedContractVersion = 6

    /// Integer from a JSON number, rejecting booleans and non-integral values.
    static func int(_ value: Any?) -> Int? {
        guard let number = value as? NSNumber, CFGetTypeID(number) != CFBooleanGetTypeID() else { return nil }
        let double = number.doubleValue
        guard double.isFinite, double >= 0, double < 9.0e15, double.rounded() == double else { return nil }
        return number.intValue
    }

    static func double(_ value: Any?) -> Double? {
        guard let number = value as? NSNumber, CFGetTypeID(number) != CFBooleanGetTypeID() else { return nil }
        let double = number.doubleValue
        return double.isFinite ? double : nil
    }

    private static func firstInt(_ object: [String: Any], _ keys: [String]) -> Int? {
        for key in keys { if let value = int(object[key]) { return value } }
        return nil
    }

    /// `server.getUsageSummary` value → today's totals for one environment.
    /// Throws when the contract version is unsupported.
    public static func summary(_ value: Any) throws -> T3UsageValue {
        guard let object = value as? [String: Any] else { throw T3UsageError.invalidResponse }
        guard int(object["contractVersion"]) == supportedContractVersion else { throw T3UsageError.unsupportedVersion }

        var totals = T3TokenTotals()
        var cost: Double?
        var costPartial = false
        var decodedAny = false
        for case let bucket as [String: Any] in (object["buckets"] as? [Any]) ?? [] {
            guard let bucketTotals = bucket["totals"] as? [String: Any],
                  let output = firstInt(bucketTotals, ["outputTokens"]) else { continue }
            let uncached = firstInt(bucketTotals, ["uncachedInputTokens"]) ?? 0
            let cached = firstInt(bucketTotals, ["cachedInputTokens", "cacheReadInputTokens"]) ?? 0
            let creation = firstInt(bucketTotals, ["cacheCreationTokens", "cacheCreationInputTokens", "cacheWriteTokens"]) ?? 0
            let reasoning = firstInt(bucketTotals, ["reasoningTokens", "reasoningOutputTokens"]) ?? 0
            totals = totals + T3TokenTotals(
                uncachedInputTokens: uncached, cachedInputTokens: cached, cacheCreationTokens: creation,
                outputTokens: output, reasoningTokens: reasoning)
            decodedAny = true
            if let bucketCost = double(bucket["costUsd"]) ?? double(bucketTotals["costUsd"]) {
                cost = (cost ?? 0) + bucketCost
            }
            if let unpriced = int(bucket["unpricedRecords"]) ?? int(bucketTotals["unpricedRecords"]), unpriced > 0 {
                costPartial = true
            }
        }

        var tokensPartial = false
        if let sources = object["sources"] as? [Any] {
            for case let source as [String: Any] in sources where (source["status"] as? String) != "ok" {
                tokensPartial = true
            }
        }
        let pricingStatus = (object["pricing"] as? [String: Any])?["status"] as? String
        if pricingStatus != "fresh" && pricingStatus != "cached" { costPartial = true }
        if tokensPartial { costPartial = true }

        // No decodable bucket on a healthy source is a real zero for today; keep it explicit.
        return T3UsageValue(
            tokens: decodedAny || !tokensPartial ? totals : nil,
            tokensPartial: tokensPartial,
            costUsd: cost,
            costPartial: costPartial || cost == nil)
    }

    /// Only the allowlisted projection keys. The rest, including the timeline, is ignored.
    public struct Projection: Equatable, Sendable {
        public struct ProviderThread: Equatable, Sendable {
            public let id: String
            public let contextUsed: Int?
            public let contextMax: Int?
        }
        public struct Turn: Equatable, Sendable {
            public let providerThreadId: String?
            public let startedAt: Date?
            public let contextUsed: Int?
            public let contextMax: Int?
            public let contextUpdatedAt: Date?
            /// nil when the turn's usage is partial, unavailable, or missing.
            public let tokens: T3TokenTotals?
        }
        public let activeProviderThreadId: String?
        public let providerThreads: [ProviderThread]
        public let turns: [Turn]
    }

    public static func projection(_ value: Any) throws -> Projection {
        guard var object = value as? [String: Any] else { throw T3UsageError.invalidResponse }
        for key in ["projection", "thread"] {
            if object["providerTurns"] == nil, let nested = object[key] as? [String: Any] { object = nested }
        }
        let providerThreads: [Projection.ProviderThread] = ((object["providerThreads"] as? [Any]) ?? []).compactMap { raw in
            guard let thread = raw as? [String: Any], let id = thread["id"] as? String else { return nil }
            let context = thread["contextUsage"] as? [String: Any]
            return Projection.ProviderThread(
                id: id, contextUsed: int(context?["usedTokens"]), contextMax: int(context?["maxTokens"]))
        }
        let turns: [Projection.Turn] = ((object["providerTurns"] as? [Any]) ?? []).compactMap { raw in
            guard let turn = raw as? [String: Any] else { return nil }
            let usage = turn["tokenUsage"] as? [String: Any]
            return Projection.Turn(
                providerThreadId: turn["providerThreadId"] as? String,
                startedAt: PresenceCardFormatter.parseDate(turn["startedAt"] as? String),
                contextUsed: int(usage?["usedTokens"]),
                contextMax: int(usage?["maxTokens"]),
                contextUpdatedAt: PresenceCardFormatter.parseDate(usage?["updatedAt"] as? String),
                tokens: turnTokens(turn["turnTokenUsage"]))
        }
        return Projection(
            activeProviderThreadId: object["activeProviderThreadId"] as? String,
            providerThreads: providerThreads,
            turns: turns)
    }

    /// Complete turns only: input already includes cache reads and writes.
    static func turnTokens(_ value: Any?) -> T3TokenTotals? {
        guard let usage = value as? [String: Any], (usage["usageStatus"] as? String) == "complete",
              let input = int(usage["inputTokens"]), let output = int(usage["outputTokens"]) else { return nil }
        let cached = int(usage["cachedInputTokens"]) ?? 0
        let creation = int(usage["cacheCreationTokens"]) ?? int(usage["cacheCreationInputTokens"]) ?? 0
        let uncached = max(0, input - cached - creation)
        return T3TokenTotals(
            uncachedInputTokens: uncached,
            cachedInputTokens: min(cached, input),
            cacheCreationTokens: max(0, min(creation, input - min(cached, input))),
            outputTokens: output,
            reasoningTokens: int(usage["reasoningTokens"]) ?? 0)
    }

    /// Sum of turns, optionally only those started at or after `since`. Any incomplete turn → partial.
    public static func tokens(_ projection: Projection, since: Date? = nil) -> T3UsageValue {
        var totals = T3TokenTotals()
        var partial = false
        for turn in projection.turns {
            if let since = since {
                guard let started = turn.startedAt else { partial = true; continue }
                if started < since { continue }
            }
            if let tokens = turn.tokens { totals = totals + tokens } else { partial = true }
        }
        return T3UsageValue(tokens: totals, tokensPartial: partial, costPartial: true)
    }

    /// Newest `tokenUsage` on the active provider thread, falling back to that thread's `contextUsage`.
    public static func context(_ projection: Projection) -> T3UsageValue? {
        guard let active = projection.activeProviderThreadId else { return nil }
        var best: Projection.Turn?
        for turn in projection.turns where turn.providerThreadId == active && turn.contextUsed != nil {
            if best == nil || (turn.contextUpdatedAt ?? .distantPast) > (best?.contextUpdatedAt ?? .distantPast) {
                best = turn
            }
        }
        if let turn = best, let used = turn.contextUsed, let maximum = turn.contextMax, maximum > 0 {
            return T3UsageValue(costPartial: true, contextUsedTokens: used, contextMaxTokens: maximum)
        }
        if let thread = projection.providerThreads.first(where: { $0.id == active }),
           let used = thread.contextUsed, let maximum = thread.contextMax, maximum > 0 {
            return T3UsageValue(costPartial: true, contextUsedTokens: used, contextMaxTokens: maximum)
        }
        return nil
    }

    /// `yyyy-MM-dd` in the given time zone.
    public static func localDay(_ date: Date, timeZone: TimeZone) -> String {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = timeZone
        let parts = calendar.dateComponents([.year, .month, .day], from: date)
        let year = parts.year ?? 1970, month = parts.month ?? 1, day = parts.day ?? 1
        return String(format: "%04ld-%02ld-%02ld", year, month, day)
    }
}

// MARK: - Client

/// Pairing exchange, secure storage, and one bounded WebSocket poll per environment.
public final class T3UsageClient: @unchecked Sendable {
    public static let pollTimeoutSeconds: TimeInterval = 20
    public static let maxMessageBytes = 2 * 1024 * 1024

    private let transport: HTTPTransporting
    private let connector: T3WebSocketConnecting
    private let keychain: KeychainManaging
    private let appVersion: String
    private let timeZone: TimeZone
    private let pollTimeout: TimeInterval

    public init(
        transport: HTTPTransporting = URLSessionHTTPTransport.shared,
        connector: T3WebSocketConnecting = URLSessionWebSocketConnector.shared,
        keychain: KeychainManaging = KeychainService.shared,
        appVersion: String = "0.1.0-preview.3",
        timeZone: TimeZone = .current,
        pollTimeout: TimeInterval = T3UsageClient.pollTimeoutSeconds
    ) {
        self.transport = transport
        self.connector = connector
        self.keychain = keychain
        self.appVersion = appVersion
        self.timeZone = timeZone
        self.pollTimeout = pollTimeout
    }

    // MARK: Credential

    public func credential(origin: String) -> T3StoredCredential? {
        try? keychain.loadCredential(forService: T3UsagePairing.keychainService, account: T3UsagePairing.keychainAccount(origin: origin))
    }

    public func removeCredential(origin: String) throws {
        try keychain.deleteCredential(forService: T3UsagePairing.keychainService, account: T3UsagePairing.keychainAccount(origin: origin))
    }

    public func permissionStatus(origin: String, now: Date = Date()) -> T3UsagePermissionStatus {
        guard let saved = credential(origin: origin), !saved.accessToken.isEmpty else { return .notAllowed }
        return saved.isExpired(at: now) ? .pairAgain : .allowed
    }

    // MARK: Pairing

    /// Exchanges a pairing code and stores `{accessToken, expiresAt}`. Never sends a DPoP header.
    @discardableResult
    public func pair(origin: String, input: String, clientOS: String = "macOS", now: Date = Date()) async throws -> T3StoredCredential {
        let code = try T3UsagePairing.parseCode(input, environmentOrigin: origin)
        guard let url = URL(string: "\(origin)/oauth/token") else { throw T3UsageError.unreachable }
        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.timeoutInterval = 10
        request.setValue("application/x-www-form-urlencoded", forHTTPHeaderField: "Content-Type")
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        request.setValue("no-store", forHTTPHeaderField: "Cache-Control")
        let parameters: [(String, String)] = [
            ("grant_type", "urn:ietf:params:oauth:grant-type:token-exchange"),
            ("subject_token", code),
            ("subject_token_type", "urn:t3:params:oauth:token-type:environment-bootstrap"),
            ("requested_token_type", "urn:ietf:params:oauth:token-type:access_token"),
            ("scope", "orchestration:read diagnostics:read"),
            ("client_label", "Deskchirp"),
            ("client_device_type", "desktop"),
            ("client_os", clientOS)
        ]
        request.httpBody = Self.formEncode(parameters)

        let exchange: (Data, HTTPURLResponse)
        do {
            exchange = try await transport.send(request: request)
        } catch {
            throw T3UsageError.unreachable
        }
        let (data, response) = exchange
        if response.statusCode == 401 || response.statusCode == 400 { throw T3UsageError.pairingCodeRejected }
        guard response.statusCode == 200, data.count <= 65_536,
              let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let token = json["access_token"] as? String, !token.isEmpty,
              let tokenType = json["token_type"] as? String,
              tokenType.caseInsensitiveCompare("Bearer") == .orderedSame,
              let expiresIn = T3UsageDecoding.int(json["expires_in"]), expiresIn > 0, expiresIn <= 31_536_000 else {
            throw T3UsageError.invalidResponse
        }
        // T3 drops scopes the code did not grant instead of failing.
        let scopes = ((json["scope"] as? String) ?? "").split(separator: " ").map(String.init)
        guard scopes.contains("diagnostics:read") else { throw T3UsageError.missingDiagnosticsScope }

        let credential = T3StoredCredential(accessToken: token, expiresAt: now.addingTimeInterval(TimeInterval(expiresIn)))
        try keychain.saveCredential(credential, forService: T3UsagePairing.keychainService,
                                    account: T3UsagePairing.keychainAccount(origin: origin))
        return credential
    }

    static func formEncode(_ parameters: [(String, String)]) -> Data {
        let allowed = CharacterSet.alphanumerics.union(CharacterSet(charactersIn: "-_.~"))
        let body = parameters.map { key, value in
            let k = key.addingPercentEncoding(withAllowedCharacters: allowed) ?? key
            let v = value.addingPercentEncoding(withAllowedCharacters: allowed) ?? value
            return "\(k)=\(v)"
        }.joined(separator: "&")
        return Data(body.utf8)
    }

    // MARK: Poll

    /// One bounded poll: environment check, one socket, the needed RPCs, then close.
    /// Throws `pairAgain` (credential deleted), `unsupportedVersion`, or a reachability error.
    public func poll(origin: String, plan: T3UsagePollPlan, now: Date = Date()) async throws -> T3EnvironmentUsageResult {
        guard !plan.isEmpty else { return T3EnvironmentUsageResult() }
        guard let saved = credential(origin: origin), !saved.accessToken.isEmpty else { throw T3UsageError.pairAgain }
        if saved.isExpired(at: now) {
            try? removeCredential(origin: origin)
            throw T3UsageError.pairAgain
        }
        try await checkEnvironment(origin: origin)

        guard let wsURL = Self.webSocketURL(origin: origin, appVersion: appVersion) else { throw T3UsageError.unreachable }
        var request = URLRequest(url: wsURL)
        request.timeoutInterval = pollTimeout
        request.setValue("Bearer \(saved.accessToken)", forHTTPHeaderField: "Authorization")

        let connector = self.connector
        let connection: T3WebSocketConnection
        do {
            connection = try await connector.connect(request: request, maxMessageBytes: Self.maxMessageBytes)
        } catch T3UsageError.pairAgain {
            try? removeCredential(origin: origin)
            throw T3UsageError.pairAgain
        }
        let session = T3RpcSession(connection: connection, maxMessageBytes: Self.maxMessageBytes)
        defer { session.close() }

        let timeZone = self.timeZone
        return try await withUsageDeadline(seconds: pollTimeout, onTimeout: { session.close() }) {
            try await Self.answer(plan: plan, session: session, timeZone: timeZone, now: now)
        }
    }

    private static func answer(plan: T3UsagePollPlan, session: T3RpcSession, timeZone: TimeZone, now: Date) async throws -> T3EnvironmentUsageResult {
        var result = T3EnvironmentUsageResult()
        if plan.needsToday {
            let day = T3UsageDecoding.localDay(now, timeZone: timeZone)
            let payload: [String: Any] = [
                "sinceDay": day, "untilDay": day, "timeZone": timeZone.identifier, "awaitRefresh": true
            ]
            do {
                let value = try await session.call(tag: "server.getUsageSummary", payload: payload)
                result.today = try? T3UsageDecoding.summary(value)
            } catch T3UsageError.valueUnavailable {
                result.today = nil
            }
        }

        var projections: [String: T3UsageDecoding.Projection] = [:]
        var wanted = plan.sessionThreadIds
        if let pinned = plan.pinnedThreadId, !wanted.contains(pinned) { wanted.append(pinned) }
        for threadId in wanted {
            do {
                let value = try await session.call(tag: "orchestration.getThreadProjection", payload: ["threadId": threadId])
                // Decode only allowlisted keys; the raw value (which carries conversation text) is dropped here.
                // A missing entry later counts as unavailable (session → partial, pinned → no value).
                projections[threadId] = try? T3UsageDecoding.projection(value)
            } catch T3UsageError.valueUnavailable {
                continue
            }
        }

        if plan.needsSession {
            var totals = T3TokenTotals()
            var partial = plan.sessionOverflow || plan.sessionStart == nil
            for threadId in plan.sessionThreadIds {
                guard let projection = projections[threadId] else { partial = true; continue }
                let value = T3UsageDecoding.tokens(projection, since: plan.sessionStart)
                totals = totals + (value.tokens ?? T3TokenTotals())
                if value.tokensPartial { partial = true }
            }
            result.session = T3UsageValue(tokens: totals, tokensPartial: partial, costPartial: true)
        }
        if let pinned = plan.pinnedThreadId, let projection = projections[pinned] {
            result.pinnedTokens = T3UsageDecoding.tokens(projection)
            result.pinnedContext = T3UsageDecoding.context(projection)
        }
        return result
    }

    private func checkEnvironment(origin: String) async throws {
        guard let url = URL(string: "\(origin)/.well-known/t3/environment") else { throw T3UsageError.unreachable }
        var request = URLRequest(url: url)
        request.httpMethod = "GET"
        request.timeoutInterval = 10
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        let exchange: (Data, HTTPURLResponse)
        do {
            exchange = try await transport.send(request: request)
        } catch {
            throw T3UsageError.unreachable
        }
        let (data, response) = exchange
        guard response.statusCode == 200,
              let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else {
            throw T3UsageError.unreachable
        }
        guard T3UsageDecoding.int(json["orchestrationProtocolVersion"]) == 2 else { throw T3UsageError.unsupportedVersion }
    }

    /// `ws(s)://host:port/ws?orchestrationProtocol=2&clientSurface=deskchirp&clientAppVersion={version}`.
    public static func webSocketURL(origin: String, appVersion: String) -> URL? {
        guard var components = URLComponents(string: origin) else { return nil }
        switch components.scheme?.lowercased() {
        case "https": components.scheme = "wss"
        case "http": components.scheme = "ws"
        default: return nil
        }
        components.path = "/ws"
        components.queryItems = [
            URLQueryItem(name: "orchestrationProtocol", value: "2"),
            URLQueryItem(name: "clientSurface", value: "deskchirp"),
            URLQueryItem(name: "clientAppVersion", value: appVersion)
        ]
        return components.url
    }
}

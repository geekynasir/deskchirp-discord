import Foundation

/// Token categories as T3 reports them. Reasoning is already inside output and is never added.
public struct T3TokenTotals: Equatable, Sendable {
    public var uncachedInputTokens: Int
    public var cachedInputTokens: Int
    public var cacheCreationTokens: Int
    public var outputTokens: Int
    public var reasoningTokens: Int

    public init(
        uncachedInputTokens: Int = 0,
        cachedInputTokens: Int = 0,
        cacheCreationTokens: Int = 0,
        outputTokens: Int = 0,
        reasoningTokens: Int = 0
    ) {
        self.uncachedInputTokens = uncachedInputTokens
        self.cachedInputTokens = cachedInputTokens
        self.cacheCreationTokens = cacheCreationTokens
        self.outputTokens = outputTokens
        self.reasoningTokens = reasoningTokens
    }

    /// Uncached input + cached input + cache creation.
    public var input: Int { uncachedInputTokens + cachedInputTokens + cacheCreationTokens }
    /// Output only; reasoning is a subset of output.
    public var output: Int { outputTokens }
    public var total: Int { input + output }

    public func value(for category: PresenceTokenCategory) -> Int {
        switch category {
        case .total: return total
        case .input: return input
        case .output: return output
        }
    }

    public static func + (lhs: T3TokenTotals, rhs: T3TokenTotals) -> T3TokenTotals {
        T3TokenTotals(
            uncachedInputTokens: lhs.uncachedInputTokens + rhs.uncachedInputTokens,
            cachedInputTokens: lhs.cachedInputTokens + rhs.cachedInputTokens,
            cacheCreationTokens: lhs.cacheCreationTokens + rhs.cacheCreationTokens,
            outputTokens: lhs.outputTokens + rhs.outputTokens,
            reasoningTokens: lhs.reasoningTokens + rhs.reasoningTokens
        )
    }
}

/// One usage value (today, session, or pinned task). Missing data stays nil, never zero.
public struct T3UsageValue: Equatable, Sendable {
    public var tokens: T3TokenTotals?
    /// Partial token data is not shown.
    public var tokensPartial: Bool
    public var costUsd: Double?
    /// Partial cost data hides the cost suffix only.
    public var costPartial: Bool
    public var contextUsedTokens: Int?
    public var contextMaxTokens: Int?

    public init(
        tokens: T3TokenTotals? = nil,
        tokensPartial: Bool = false,
        costUsd: Double? = nil,
        costPartial: Bool = false,
        contextUsedTokens: Int? = nil,
        contextMaxTokens: Int? = nil
    ) {
        self.tokens = tokens
        self.tokensPartial = tokensPartial
        self.costUsd = costUsd
        self.costPartial = costPartial
        self.contextUsedTokens = contextUsedTokens
        self.contextMaxTokens = contextMaxTokens
    }

    /// Floor of used × 100 / max, or nil when max is missing or zero.
    public var contextPercent: Int? {
        guard let used = contextUsedTokens, let maximum = contextMaxTokens, maximum > 0, used >= 0 else { return nil }
        return min(100, used.multipliedReportingOverflow(by: 100).overflow ? 100 : used * 100 / maximum)
    }
}

/// Combined token usage across environments with the usage permission. TTL 180 seconds.
public struct T3UsageSnapshot: Equatable, Sendable {
    public static let ttlSeconds: TimeInterval = 180

    public let observedAt: Date
    public let today: T3UsageValue?
    public let session: T3UsageValue?
    public let pinnedTask: T3UsageValue?
    /// The `"{environmentId}/{threadId}"` the pinned value was computed for.
    public let pinnedTaskKey: String?
    /// Work-session start used for the session value.
    public let sessionStart: Date?

    public init(
        observedAt: Date,
        today: T3UsageValue? = nil,
        session: T3UsageValue? = nil,
        pinnedTask: T3UsageValue? = nil,
        pinnedTaskKey: String? = nil,
        sessionStart: Date? = nil
    ) {
        self.observedAt = observedAt
        self.today = today
        self.session = session
        self.pinnedTask = pinnedTask
        self.pinnedTaskKey = pinnedTaskKey
        self.sessionStart = sessionStart
    }

    public func isFresh(maxAgeSeconds: TimeInterval = T3UsageSnapshot.ttlSeconds, now: Date = Date()) -> Bool {
        let age = now.timeIntervalSince(observedAt)
        return age >= 0 && age <= maxAgeSeconds
    }
}

/// Plain-language usage permission state per environment (spec/presence-v3.md §6).
public enum T3UsagePermissionStatus: Equatable, Sendable {
    case notAllowed
    case pairing
    case allowed
    case pairAgain
    case unsupportedVersion
    case failed(String)

    public var title: String {
        switch self {
        case .notAllowed: return "Token usage is off"
        case .pairing: return "Pairing…"
        case .allowed: return "Token usage allowed"
        case .pairAgain: return "Pair again to keep showing tokens"
        case .unsupportedVersion: return "This T3 version isn’t supported for token usage."
        case .failed(let message): return message
        }
    }

    public var isAllowed: Bool {
        if case .allowed = self { return true }
        return false
    }
}

/// Number and cost formatting from spec/presence-v3.md §5.3, using integer arithmetic.
public enum PresenceNumberFormat {
    public static func compact(_ value: Int) -> String {
        let n = max(0, value)
        if n < 1000 { return String(n) }
        let units: [(Int, String)] = [(1_000, "K"), (1_000_000, "M"), (1_000_000_000, "B")]
        var index = 0
        while index + 1 < units.count && n >= units[index + 1].0 { index += 1 }
        while index < units.count {
            let (unit, suffix) = units[index]
            let (scaled, overflow) = n.multipliedReportingOverflow(by: 10)
            if !overflow {
                let tenths = (scaled + unit / 2) / unit
                if tenths < 1000 {
                    let whole = tenths / 10
                    let fraction = tenths % 10
                    return fraction == 0 ? "\(whole)\(suffix)" : "\(whole).\(fraction)\(suffix)"
                }
            }
            let whole = n / unit + ((n % unit) >= (unit - unit / 2) ? 1 : 0)
            if whole >= 1000 && index + 1 < units.count {
                index += 1
                continue
            }
            return "\(whole)\(suffix)"
        }
        return String(n)
    }

    public static func exact(_ value: Int) -> String {
        let digits = String(max(0, value))
        var result = ""
        for (offset, character) in digits.enumerated() {
            if offset > 0 && (digits.count - offset) % 3 == 0 { result.append(",") }
            result.append(character)
        }
        return result
    }

    public static func tokens(_ value: Int, rounding: PresenceTokenRounding) -> String {
        switch rounding {
        case .compact: return compact(value)
        case .exact: return exact(value)
        }
    }

    /// `~$1.84`, `~$10`, `~$23`. Nil for negative or non-finite values.
    public static func cost(_ usd: Double) -> String? {
        guard usd.isFinite, usd >= 0, usd < 1e12 else { return nil }
        let cents = Int((usd * 100).rounded(.toNearestOrAwayFromZero))
        if cents < 1000 {
            let fraction = cents % 100
            return "~$\(cents / 100).\(fraction < 10 ? "0" : "")\(fraction)"
        }
        return "~$\((cents + 50) / 100)"
    }
}

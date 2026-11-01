import Foundation

/// One environment's part of a usage poll.
public struct T3UsageEnvironmentRequest: Equatable, Sendable {
    public let environmentId: String
    public let origin: String
    public let plan: T3UsagePollPlan

    public init(environmentId: String, origin: String, plan: T3UsagePollPlan) {
        self.environmentId = environmentId
        self.origin = origin
        self.plan = plan
    }
}

/// Combines per-environment usage into one snapshot and tracks each environment's permission state.
/// Polling cadence (60 s while `cardStyle = usage`) is owned by the coordinator.
public final class T3UsageService: @unchecked Sendable {
    public static let pollIntervalSeconds: TimeInterval = 60
    /// At most this many thread projections per poll, across all environments.
    public static let maxProjectionsPerPoll = 24

    public let client: T3UsageClient
    private let lock = NSLock()
    private var statuses: [String: T3UsagePermissionStatus] = [:]

    public init(client: T3UsageClient = T3UsageClient()) {
        self.client = client
    }

    /// Permission state for an environment, from the last poll or the saved credential.
    public func status(environmentId: String, origin: String, now: Date = Date()) -> T3UsagePermissionStatus {
        lock.lock()
        let remembered = statuses[environmentId]
        lock.unlock()
        let saved = client.permissionStatus(origin: origin, now: now)
        switch (remembered, saved) {
        case (.some(.pairAgain), .notAllowed): return .pairAgain
        case (.some(.unsupportedVersion), .allowed): return .unsupportedVersion
        case (.some(.pairing), _): return .pairing
        case (.some(.failed(let message)), .notAllowed): return .failed(message)
        default: return saved
        }
    }

    public func pair(environmentId: String, origin: String, input: String) async throws {
        setStatus(.pairing, for: environmentId)
        do {
            try await client.pair(origin: origin, input: input)
            setStatus(nil, for: environmentId)
        } catch let error as T3UsageError {
            setStatus(.failed(error.errorDescription ?? "Pairing didn’t finish."), for: environmentId)
            throw error
        } catch {
            setStatus(.failed("Couldn’t save token access to Keychain. Try pairing again."), for: environmentId)
            throw error
        }
    }

    /// **Remove token access**, also used when the environment is removed.
    public func removeAccess(environmentId: String, origin: String) throws {
        try client.removeCredential(origin: origin)
        setStatus(nil, for: environmentId)
    }

    private func setStatus(_ status: T3UsagePermissionStatus?, for environmentId: String) {
        lock.lock()
        statuses[environmentId] = status
        lock.unlock()
    }

    /// Builds per-environment plans from the work threads (exclusions already applied).
    /// Session threads are running, waiting and starting top-level threads plus their helpers.
    public static func plans(
        settings: PresenceSettings,
        work: [T3WorkThread],
        allowedEnvironments: [T3Environment],
        sessionStart: Date?
    ) -> [T3UsageEnvironmentRequest] {
        let scope = PresenceCardFormatter.effectiveScope(settings)
        var budget = maxProjectionsPerPoll
        var requests: [T3UsageEnvironmentRequest] = []
        let overallSessionKeys = sessionThreadKeys(work: work)
        let pinnedEnvironmentId = settings.pinnedTask.split(separator: "/", maxSplits: 1).first.map(String.init)
        let pinnedThreadId = settings.pinnedTask.split(separator: "/", maxSplits: 1).dropFirst().first.map(String.init)
        if scope == .pinnedTask && pinnedThreadId != nil { budget -= 1 }

        for environment in allowedEnvironments {
            guard let origin = environment.endpoint?.origin else { continue }
            var plan = T3UsagePollPlan(sessionStart: sessionStart)
            switch scope {
            case .today:
                plan.needsToday = true
            case .session:
                let keys = overallSessionKeys.filter { $0.hasPrefix(environment.id + "/") }
                let ids = keys.map { String($0.dropFirst(environment.id.count + 1)) }
                let taken = Array(ids.prefix(max(0, budget)))
                budget -= taken.count
                plan.sessionThreadIds = taken
                plan.sessionOverflow = taken.count < ids.count
            case .pinnedTask:
                if pinnedEnvironmentId == environment.id { plan.pinnedThreadId = pinnedThreadId }
            }
            if !plan.isEmpty {
                requests.append(T3UsageEnvironmentRequest(environmentId: environment.id, origin: origin, plan: plan))
            }
        }
        return requests
    }

    static func sessionThreadKeys(work: [T3WorkThread]) -> [String] {
        var topLevel = Set<String>()
        var ordered: [String] = []
        for item in work where item.thread.isTask && item.thread.isUnfinished {
            topLevel.insert(item.threadKey)
            ordered.append(item.threadKey)
        }
        for item in work where item.thread.isHelper {
            guard let parent = item.thread.parentThreadId,
                  topLevel.contains("\(item.environmentId)/\(parent)") else { continue }
            ordered.append(item.threadKey)
        }
        var seen = Set<String>()
        return ordered.filter { seen.insert($0).inserted }
    }

    /// Polls every request and combines them. Any eligible environment failing makes the value partial.
    /// Returns nil when nothing could be read.
    public func poll(
        requests: [T3UsageEnvironmentRequest],
        pinnedTaskKey: String?,
        sessionStart: Date?,
        now: Date = Date()
    ) async -> T3UsageSnapshot? {
        guard !requests.isEmpty else { return nil }
        var today: T3UsageValue?
        var todayFailed = false
        var session: T3UsageValue?
        var sessionFailed = false
        var pinned: T3UsageValue?
        var anySuccess = false

        for request in requests {
            do {
                let result = try await client.poll(origin: request.origin, plan: request.plan, now: now)
                anySuccess = true
                setStatus(nil, for: request.environmentId)
                if request.plan.needsToday {
                    if let value = result.today { today = Self.combine(today, value) } else { todayFailed = true }
                }
                if request.plan.needsSession {
                    if let value = result.session { session = Self.combine(session, value) } else { sessionFailed = true }
                }
                if request.plan.pinnedThreadId != nil {
                    var value = result.pinnedTokens ?? T3UsageValue(tokensPartial: true, costPartial: true)
                    value.contextUsedTokens = result.pinnedContext?.contextUsedTokens
                    value.contextMaxTokens = result.pinnedContext?.contextMaxTokens
                    pinned = value
                }
            } catch {
                if request.plan.needsToday { todayFailed = true }
                if request.plan.needsSession { sessionFailed = true }
                switch error as? T3UsageError {
                case .some(.pairAgain): setStatus(.pairAgain, for: request.environmentId)
                case .some(.unsupportedVersion): setStatus(.unsupportedVersion, for: request.environmentId)
                default: break
                }
            }
        }
        guard anySuccess else { return nil }
        if todayFailed, var value = today {
            value.tokensPartial = true
            value.costPartial = true
            today = value
        }
        if sessionFailed, var value = session {
            value.tokensPartial = true
            session = value
        }
        return T3UsageSnapshot(
            observedAt: now, today: today, session: session, pinnedTask: pinned,
            pinnedTaskKey: pinnedTaskKey, sessionStart: sessionStart)
    }

    static func combine(_ lhs: T3UsageValue?, _ rhs: T3UsageValue) -> T3UsageValue {
        guard let lhs = lhs else { return rhs }
        let tokens: T3TokenTotals?
        if let a = lhs.tokens, let b = rhs.tokens { tokens = a + b } else { tokens = lhs.tokens ?? rhs.tokens }
        let cost: Double?
        if let a = lhs.costUsd, let b = rhs.costUsd { cost = a + b } else { cost = nil }
        return T3UsageValue(
            tokens: tokens,
            tokensPartial: lhs.tokensPartial || rhs.tokensPartial || lhs.tokens == nil || rhs.tokens == nil,
            costUsd: cost,
            costPartial: lhs.costPartial || rhs.costPartial || cost == nil)
    }
}

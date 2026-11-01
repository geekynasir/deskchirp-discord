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
    /// Bumped by removal; a pairing that started under an older generation may not store its result.
    private var generations: [String: Int] = [:]
    /// Serializes credential save/delete with generation checks.
    private let credentialLock = NSLock()

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
        let generation = currentGeneration(environmentId)
        setStatus(.pairing, for: environmentId)
        do {
            let credential = try await client.exchange(origin: origin, input: input)
            // Save only if the environment wasn't removed (or access removed) while the exchange ran.
            credentialLock.lock()
            defer { credentialLock.unlock() }
            guard currentGeneration(environmentId) == generation else { throw T3UsageError.pairingCancelled }
            try client.saveCredential(credential, origin: origin)
            setStatus(nil, for: environmentId)
        } catch T3UsageError.pairingCancelled {
            throw T3UsageError.pairingCancelled
        } catch let error as T3UsageError {
            if currentGeneration(environmentId) == generation {
                setStatus(.failed(error.errorDescription ?? "Pairing didn’t finish."), for: environmentId)
            }
            throw error
        } catch {
            if currentGeneration(environmentId) == generation {
                setStatus(.failed("Couldn’t save token access to Keychain. Try pairing again."), for: environmentId)
            }
            throw error
        }
    }

    /// **Remove token access**, also used when the environment is removed. Cancels a pending pairing.
    public func removeAccess(environmentId: String, origin: String) throws {
        credentialLock.lock()
        defer { credentialLock.unlock() }
        lock.lock()
        generations[environmentId, default: 0] += 1
        lock.unlock()
        try client.removeCredential(origin: origin)
        setStatus(nil, for: environmentId)
    }

    private func currentGeneration(_ environmentId: String) -> Int {
        lock.lock()
        defer { lock.unlock() }
        return generations[environmentId] ?? 0
    }

    private func setStatus(_ status: T3UsagePermissionStatus?, for environmentId: String) {
        lock.lock()
        statuses[environmentId] = status
        lock.unlock()
    }

    /// Builds per-environment plans.
    /// - Parameter work: threads after exclusions (helpers of excluded parents removed), **before** the
    ///   pinned-project restriction. The pinned task is requested only while it is present here, so an
    ///   excluded or vanished task never has its usage read or shown.
    /// Session threads are running, waiting and starting top-level threads plus their helpers, restricted to
    /// the pinned project when it is present. Session threads in environments without the usage permission
    /// make the session value partial.
    public static func plans(
        settings: PresenceSettings,
        work: [T3WorkThread],
        allowedEnvironments: [T3Environment],
        sessionStart: Date?
    ) -> [T3UsageEnvironmentRequest] {
        let scope = PresenceCardFormatter.effectiveScope(settings)
        var budget = maxProjectionsPerPoll
        var requests: [T3UsageEnvironmentRequest] = []

        // A pinned project always restricts what is counted.
        let sessionWork = settings.pinnedProject.isEmpty ? work : work.filter { $0.projectKey == settings.pinnedProject }
        let overallSessionKeys = sessionThreadKeys(work: sessionWork)
        let allowedIds = Set(allowedEnvironments.map { $0.id })
        let sessionMissingPermission = overallSessionKeys.contains { key in
            !allowedIds.contains(where: { key.hasPrefix($0 + "/") })
        }

        let pinnedVisible = isPinnedTaskVisible(settings.pinnedTask, work: work)
        let pinnedEnvironmentId = settings.pinnedTask.split(separator: "/", maxSplits: 1).first.map(String.init)
        let pinnedThreadId = pinnedVisible
            ? settings.pinnedTask.split(separator: "/", maxSplits: 1).dropFirst().first.map(String.init)
            : nil
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
                // Partial when capped, or when some session work lives where usage can't be read.
                plan.sessionOverflow = taken.count < ids.count || (!taken.isEmpty && sessionMissingPermission)
            case .pinnedTask:
                if pinnedEnvironmentId == environment.id { plan.pinnedThreadId = pinnedThreadId }
            }
            if !plan.isEmpty {
                requests.append(T3UsageEnvironmentRequest(environmentId: environment.id, origin: origin, plan: plan))
            }
        }
        return requests
    }

    /// True only when fresh, exclusion-filtered metadata confirms the pinned task — and its parent, if it
    /// has one — is present and not excluded. Missing metadata (no snapshot, stale or absent environment,
    /// unknown parent) means unavailable.
    public static func isPinnedTaskVisible(_ pinnedTask: String, work: [T3WorkThread]) -> Bool {
        guard !pinnedTask.isEmpty, let pinned = work.first(where: { $0.threadKey == pinnedTask }) else { return false }
        guard let parent = pinned.thread.parentThreadId, !parent.isEmpty else { return true }
        let parentKey = "\(pinned.environmentId)/\(parent)"
        return work.contains { $0.threadKey == parentKey }
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

        // Every environment is polled concurrently, so a slow one can't hold up the healthy ones.
        // Each poll is bounded by the client's own deadline.
        let client = self.client
        var outcomes: [Int: Result<T3EnvironmentUsageResult, Error>] = [:]
        await withTaskGroup(of: (Int, Result<T3EnvironmentUsageResult, Error>).self) { group in
            for (index, request) in requests.enumerated() {
                group.addTask {
                    do {
                        return (index, .success(try await client.poll(origin: request.origin, plan: request.plan, now: now)))
                    } catch {
                        return (index, .failure(error))
                    }
                }
            }
            for await (index, outcome) in group { outcomes[index] = outcome }
        }

        for (index, request) in requests.enumerated() {
            switch outcomes[index] ?? .failure(T3UsageError.unreachable) {
            case .success(let result):
                anySuccess = true
                setStatus(nil, for: request.environmentId)
                if request.plan.needsToday {
                    if let value = result.today { today = Self.combine(today, value) } else { todayFailed = true }
                }
                if request.plan.needsSession {
                    if let value = result.session { session = Self.combine(session, value) } else { sessionFailed = true }
                }
                if request.plan.pinnedThreadId != nil {
                    // Context is judged on its own used/max; partial turn tokens don't hide it.
                    var value = result.pinnedTokens ?? T3UsageValue(tokensPartial: true, costPartial: true)
                    value.contextUsedTokens = result.pinnedContext?.contextUsedTokens
                    value.contextMaxTokens = result.pinnedContext?.contextMaxTokens
                    pinned = value
                }
            case .failure(let error):
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

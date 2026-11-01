import Foundation

/// Pure aggregation over a combined multi-environment snapshot (spec/presence-v3.md §1, §2, §4).
/// Thread identity is `(environmentId, threadId)`; exclusions apply before any counting.
public enum T3WorkAggregator {
    /// Threads from fresh environments with exclusions applied. Stale environments contribute nothing.
    public static func workThreads(
        snapshot: T3CombinedSnapshot?,
        excludedProjects: Set<String>,
        maxAgeSeconds: TimeInterval = 45.0,
        now: Date
    ) -> [T3WorkThread] {
        guard let snapshot = snapshot else { return [] }
        var result: [T3WorkThread] = []
        for environment in snapshot.freshEnvironments(maxAgeSeconds: maxAgeSeconds, now: now) {
            var instanceToTool: [String: String] = [:]
            for provider in environment.providers {
                instanceToTool[provider.providerInstanceId] = T3DriverKind.mapDriverKind(provider.driverKind).publicName
            }
            let excludedIds = excludedThreadIds(environment: environment, excludedProjects: excludedProjects)
            for thread in environment.threads where !excludedIds.contains(thread.threadId) {
                let tool = instanceToTool[thread.providerInstanceId] ?? T3DriverKind.otherToolsName
                result.append(T3WorkThread(environmentId: environment.environmentId, thread: thread, toolName: tool))
            }
        }
        return result
    }

    /// Threads whose project is excluded, plus helpers whose parent chain resolves to an excluded thread.
    static func excludedThreadIds(environment: T3EnvironmentSnapshot, excludedProjects: Set<String>) -> Set<String> {
        var excluded = Set<String>()
        guard !excludedProjects.isEmpty else { return excluded }
        for thread in environment.threads {
            if let projectId = thread.projectId, excludedProjects.contains("\(environment.environmentId)/\(projectId)") {
                excluded.insert(thread.threadId)
            }
        }
        // Propagate to helpers; bounded by the number of threads so cycles cannot loop forever.
        var changed = !excluded.isEmpty
        var rounds = 0
        while changed && rounds <= environment.threads.count {
            changed = false
            rounds += 1
            for thread in environment.threads where !excluded.contains(thread.threadId) {
                if let parent = thread.parentThreadId, excluded.contains(parent) {
                    excluded.insert(thread.threadId)
                    changed = true
                }
            }
        }
        return excluded
    }

    /// A pinned project restricts counts when it is present in the snapshot.
    public static func restrict(
        _ threads: [T3WorkThread],
        toPinnedProject pinnedProject: String,
        snapshot: T3CombinedSnapshot?
    ) -> [T3WorkThread] {
        guard !pinnedProject.isEmpty else { return threads }
        let inThreads = threads.contains { $0.projectKey == pinnedProject }
        let inProjects = snapshot?.environments.contains { environment in
            environment.projects.contains { "\(environment.environmentId)/\($0.projectId)" == pinnedProject }
        } ?? false
        guard inThreads || inProjects else { return threads }
        return threads.filter { $0.projectKey == pinnedProject }
    }

    public static func toolCounts(_ threads: [T3WorkThread]) -> [String: T3DriverCounts] {
        var counts: [String: T3DriverCounts] = [:]
        for work in threads {
            let thread = work.thread
            guard thread.isUnfinished else { continue }
            var value = counts[work.toolName] ?? T3DriverCounts()
            if T3ThreadStatus.isRunning(thread.status) {
                if thread.isHelper { value.runningHelpers += 1 } else { value.runningTasks += 1 }
            } else if T3ThreadStatus.isWaiting(thread.status) {
                if thread.isHelper { value.waitingHelpers += 1 } else { value.waitingTasks += 1 }
            } else if T3ThreadStatus.isStarting(thread.status) {
                if thread.isHelper { value.startingHelpers += 1 } else { value.startingTasks += 1 }
            }
            counts[work.toolName] = value
        }
        return counts
    }

    /// Project with the most running work for the featured tool. Ties keep `previous`; none → nil.
    public static func featuredProject(
        _ threads: [T3WorkThread],
        tool: String,
        previous: String?
    ) -> String? {
        var running: [String: Int] = [:]
        var unfinished: [String: Int] = [:]
        for work in threads where work.toolName == tool && work.thread.isUnfinished {
            guard let key = work.projectKey else { continue }
            unfinished[key, default: 0] += 1
            if T3ThreadStatus.isRunning(work.thread.status) { running[key, default: 0] += 1 }
        }
        let scores = running.isEmpty ? unfinished : running
        guard let top = scores.values.max(), top > 0 else { return nil }
        if let previous = previous, scores[previous] == top { return previous }
        return scores.filter { $0.value == top }.keys.sorted().first
    }
}

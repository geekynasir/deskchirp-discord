import Foundation

/// Selects the most active agent tool per specification:
/// - Most-active-now selects the tool with the most confirmed running tasks and helpers.
/// - Ties retain the current winner.
/// - A new winner needs a sustained 10-second lead unless the current winner has no running work.
/// - If no running work, falls back to waiting work, then starting work.
public final class T3WinnerSelector: @unchecked Sendable {
    private let lock = NSLock()
    private var currentWinner: String?
    private var challenger: String?
    private var challengerFirstSeenTime: Date?

    public init() {}

    public func reset() {
        lock.lock()
        defer { lock.unlock() }
        currentWinner = nil
        challenger = nil
        challengerFirstSeenTime = nil
    }

    /// Evaluates threads and provider catalog, aggregates counts, and selects the winner with hysteresis.
    public func evaluate(
        providers: [T3ProviderInfo],
        threads: [T3ThreadItem],
        now: Date = Date()
    ) -> (snapshot: T3ActivitySnapshot, winner: String?) {
        lock.lock()
        defer { lock.unlock() }

        // 1. Build mapping: providerInstanceId -> generic public tool name
        var instanceToTool: [String: String] = [:]
        for p in providers {
            let (name, _) = T3DriverKind.mapDriverKind(p.driverKind)
            instanceToTool[p.providerInstanceId] = name
        }

        // 2. Aggregate counts per tool
        var toolCounts: [String: T3DriverCounts] = [:]
        var seenThreadIds = Set<String>()

        for thread in threads {
            // Deduplicate thread IDs within the environment per specification
            guard !seenThreadIds.contains(thread.threadId) else {
                continue
            }
            seenThreadIds.insert(thread.threadId)

            // Ignore terminal and idle rows per specification
            if T3ThreadStatus.isTerminalOrIdle(thread.status) {
                continue
            }

            let toolName = instanceToTool[thread.providerInstanceId] ?? T3DriverKind.otherToolsName
            var counts = toolCounts[toolName] ?? T3DriverCounts()

            if T3ThreadStatus.isRunning(thread.status) {
                if thread.isHelper {
                    counts.runningHelpers += 1
                } else {
                    counts.runningTasks += 1
                }
            } else if T3ThreadStatus.isWaiting(thread.status) {
                if thread.isHelper {
                    counts.waitingHelpers += 1
                } else {
                    counts.waitingTasks += 1
                }
            } else if T3ThreadStatus.isStarting(thread.status) {
                if thread.isHelper {
                    counts.startingHelpers += 1
                } else {
                    counts.startingTasks += 1
                }
            }

            toolCounts[toolName] = counts
        }

        let snapshot = T3ActivitySnapshot(observedAt: now, toolCounts: toolCounts)

        // 3. Winner selection with 10-second hysteresis
        // Find tool with highest activeScore (running work)
        var bestRunningTool: String? = nil
        var bestRunningScore = 0

        // Sort keys for deterministic tie-breaking when no current winner
        let sortedToolNames = toolCounts.keys.sorted()

        for name in sortedToolNames {
            let score = toolCounts[name]?.activeScore ?? 0
            if score > bestRunningScore {
                bestRunningScore = score
                bestRunningTool = name
            } else if score == bestRunningScore && score > 0 {
                // If tied and name is the current winner, prefer current winner
                if name == currentWinner {
                    bestRunningTool = name
                }
            }
        }

        if bestRunningScore > 0, let candidate = bestRunningTool {
            let currentWinnerScore = currentWinner.flatMap { toolCounts[$0]?.activeScore } ?? 0

            if currentWinner == nil || currentWinnerScore == 0 {
                // Current winner has no running work: candidate wins immediately
                currentWinner = candidate
                challenger = nil
                challengerFirstSeenTime = nil
            } else {
                // Current winner has running work
                if candidate == currentWinner {
                    // Current winner is leading or tied: retain winner and reset challenger
                    challenger = nil
                    challengerFirstSeenTime = nil
                } else if bestRunningScore > currentWinnerScore {
                    // Candidate has a strictly higher score: needs sustained 10-second lead
                    if challenger == candidate, let firstSeen = challengerFirstSeenTime {
                        if now.timeIntervalSince(firstSeen) >= 10.0 {
                            // Sustained 10-second lead achieved!
                            currentWinner = candidate
                            challenger = nil
                            challengerFirstSeenTime = nil
                        }
                    } else {
                        // New challenger taking the lead
                        challenger = candidate
                        challengerFirstSeenTime = now
                    }
                } else {
                    // Tie or candidate is lower than current winner: ties retain current winner
                    challenger = nil
                    challengerFirstSeenTime = nil
                }
            }
        } else {
            // No running work anywhere
            challenger = nil
            challengerFirstSeenTime = nil

            // Fallback 1: Highest waiting work
            var bestWaitingTool: String? = nil
            var bestWaitingScore = 0

            for name in sortedToolNames {
                let score = toolCounts[name]?.waitingScore ?? 0
                if score > bestWaitingScore {
                    bestWaitingScore = score
                    bestWaitingTool = name
                } else if score == bestWaitingScore && score > 0 && name == currentWinner {
                    bestWaitingTool = name
                }
            }

            if bestWaitingScore > 0 {
                currentWinner = bestWaitingTool
            } else {
                // Fallback 2: Highest starting work
                var bestStartingTool: String? = nil
                var bestStartingScore = 0

                for name in sortedToolNames {
                    let score = toolCounts[name]?.startingScore ?? 0
                    if score > bestStartingScore {
                        bestStartingScore = score
                        bestStartingTool = name
                    } else if score == bestStartingScore && score > 0 && name == currentWinner {
                        bestStartingTool = name
                    }
                }

                if bestStartingScore > 0 {
                    currentWinner = bestStartingTool
                } else {
                    currentWinner = nil
                }
            }
        }

        return (snapshot, currentWinner)
    }

    public var winningTool: String? {
        lock.lock()
        defer { lock.unlock() }
        return currentWinner
    }
}

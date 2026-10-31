import Foundation

/// Formats safe T3 Rich Presence payloads matching spec/t3-activity.md and product language rules:
/// - Details: "T3 Code · {tool}" or "Using T3 Code" fallback
/// - State: "{tasks} task(s) · {helpers} helper(s)" for running;
///          "{tasks} task(s) waiting · {helpers} helper(s) waiting" for waiting;
///          "{tasks} task(s) starting · {helpers} helper(s) starting" for starting.
/// - Assets: optional t3_code large image, harness_* small image.
public enum T3ActivityFormatter {
    public static func format(
        snapshot: T3ActivitySnapshot?,
        winningTool: String?,
        showAppIcons: Bool = false,
        now: Date = Date()
    ) -> T3FormattedActivity {
        // If snapshot is missing, stale (TTL > 45s), or no winning tool: fallback to app-name-only
        guard let snapshot = snapshot,
              snapshot.isFresh(maxAgeSeconds: 45.0, now: now),
              let winner = winningTool,
              ["Claude Code", "Codex", "Antigravity", "Cursor", "OpenCode", "Grok", "Muse", "Pi", "Other tools"].contains(winner),
              let counts = snapshot.toolCounts[winner],
              counts.totalWork > 0 else {
            let assets = showAppIcons ? DiscordActivityAssets(largeImage: "t3_code") : nil
            return T3FormattedActivity(
                details: "Using T3 Code",
                state: nil,
                assets: assets,
                winningTool: nil
            )
        }

        let stateString: String?
        if counts.activeScore > 0 {
            // Rule 1: Running
            stateString = formatRunningCountLine(tasks: counts.runningTasks, helpers: counts.runningHelpers)
        } else if counts.waitingScore > 0 {
            // Rule 2: Waiting
            stateString = formatWaitingCountLine(tasks: counts.waitingTasks, helpers: counts.waitingHelpers)
        } else if counts.startingScore > 0 {
            // Rule 3: Starting
            stateString = formatStartingCountLine(tasks: counts.startingTasks, helpers: counts.startingHelpers)
        } else {
            stateString = nil
        }

        if stateString == nil {
            let assets = showAppIcons ? DiscordActivityAssets(largeImage: "t3_code") : nil
            return T3FormattedActivity(
                details: "Using T3 Code",
                state: nil,
                assets: assets,
                winningTool: nil
            )
        }

        let detailsString = "T3 Code · \(winner)"

        let assets: DiscordActivityAssets?
        if showAppIcons {
            let smallImage = assetKeyForTool(winner)
            assets = DiscordActivityAssets(largeImage: "t3_code", smallImage: smallImage)
        } else {
            assets = nil
        }

        return T3FormattedActivity(
            details: detailsString,
            state: stateString,
            assets: assets,
            winningTool: winner
        )
    }

    private static func formatRunningCountLine(tasks: Int, helpers: Int) -> String? {
        if tasks > 0 && helpers > 0 {
            let taskText = "\(tasks) task\(tasks == 1 ? "" : "s")"
            let helperText = "\(helpers) helper\(helpers == 1 ? "" : "s")"
            return "\(taskText) · \(helperText)"
        } else if tasks > 0 {
            return "\(tasks) task\(tasks == 1 ? "" : "s")"
        } else if helpers > 0 {
            return "\(helpers) helper\(helpers == 1 ? "" : "s")"
        }
        return nil
    }

    private static func formatWaitingCountLine(tasks: Int, helpers: Int) -> String? {
        if tasks > 0 && helpers > 0 {
            let taskText = "\(tasks) task\(tasks == 1 ? "" : "s") waiting"
            let helperText = "\(helpers) helper\(helpers == 1 ? "" : "s") waiting"
            return "\(taskText) · \(helperText)"
        } else if tasks > 0 {
            return "\(tasks) task\(tasks == 1 ? "" : "s") waiting"
        } else if helpers > 0 {
            return "\(helpers) helper\(helpers == 1 ? "" : "s") waiting"
        }
        return nil
    }

    private static func formatStartingCountLine(tasks: Int, helpers: Int) -> String? {
        if tasks > 0 && helpers > 0 {
            let taskText = "\(tasks) task\(tasks == 1 ? "" : "s") starting"
            let helperText = "\(helpers) helper\(helpers == 1 ? "" : "s") starting"
            return "\(taskText) · \(helperText)"
        } else if tasks > 0 {
            return "\(tasks) task\(tasks == 1 ? "" : "s") starting"
        } else if helpers > 0 {
            return "\(helpers) helper\(helpers == 1 ? "" : "s") starting"
        }
        return nil
    }

    private static func assetKeyForTool(_ toolName: String) -> String? {
        switch toolName {
        case "Claude Code":
            return "harness_claude"
        case "Codex":
            return "harness_codex"
        case "Antigravity":
            return "harness_antigravity"
        default:
            return nil
        }
    }
}

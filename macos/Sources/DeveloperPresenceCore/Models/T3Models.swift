import Foundation

/// Plain-language T3 Code connection status matching product language specifications.
public enum T3ConnectionStatus: String, Codable, Equatable, Sendable {
    case notConnected = "Not connected"
    case openingSignIn = "Opening sign-in…"
    case connected = "Connected"
    case signInAgain = "Sign in again"
    case couldNotReach = "Couldn’t reach T3 Code"

    /// Actionable recovery / guidance line displayed under status.
    public var recoveryText: String {
        switch self {
        case .notConnected:
            return "Deskchirp finds T3 Code on this computer. Allow access once to share task and helper counts."
        case .openingSignIn:
            return "Approve access in your browser, then come back here."
        case .connected:
            return "Activity is checked only while T3 Code is the active app."
        case .signInAgain:
            return "Your T3 Code sign-in expired. Connect again to keep showing activity."
        case .couldNotReach:
            return "Check that T3 Code is running and the address is right."
        }
    }
}

/// Known driver kinds mapping to generic, public agent tool names and optional asset icons.
/// Never echoes custom, unknown, or account strings to public presence.
public enum T3DriverKind {
    public static let otherToolsName = "Other tools"

    public static func mapDriverKind(_ driverKind: String) -> (publicName: String, assetKey: String?) {
        switch driverKind {
        case "claudeAgent":
            return ("Claude Code", "harness_claude")
        case "codex":
            return ("Codex", "harness_codex")
        case "antigravity":
            return ("Antigravity", "harness_antigravity")
        case "cursor":
            return ("Cursor", nil)
        case "opencode":
            return ("OpenCode", nil)
        case "grok":
            return ("Grok", nil)
        case "muse":
            return ("Muse", nil)
        case "pi":
            return ("Pi", nil)
        default:
            return (otherToolsName, nil)
        }
    }
}

/// Helper classifying thread execution status per specification:
/// Only 'running' counts as running. Waiting and starting/queued are separate.
/// Terminal and idle rows do not count.
public enum T3ThreadStatus {
    public static func isRunning(_ status: String) -> Bool {
        status == "running"
    }

    public static func isWaiting(_ status: String) -> Bool {
        status == "waiting"
    }

    public static func isStarting(_ status: String) -> Bool {
        status == "starting" || status == "queued" || status == "preparing"
    }

    public static func isTerminalOrIdle(_ status: String) -> Bool {
        status == "idle" ||
        status == "completed" ||
        status == "interrupted" ||
        status == "failed" ||
        status == "cancelled" ||
        status == "rolled_back"
    }
}

/// Stripped thread metadata decoded from T3 Code MCP thread list.
/// Ignores all prompts, titles, files, paths, and URLs for privacy.
public struct T3ThreadItem: Codable, Equatable, Sendable {
    public let threadId: String
    public let providerInstanceId: String
    public let relationshipToParent: String?
    public let status: String

    public init(
        threadId: String,
        providerInstanceId: String,
        relationshipToParent: String? = nil,
        status: String
    ) {
        self.threadId = threadId
        self.providerInstanceId = providerInstanceId
        self.relationshipToParent = relationshipToParent
        self.status = status
    }

    /// `relationshipToParent == "subagent"` means helper; fork or nil remains a task.
    public var isHelper: Bool {
        relationshipToParent == "subagent"
    }

    public var isTask: Bool {
        !isHelper
    }
}

/// Provider catalog item decoded from orchestrator_capabilities.
public struct T3ProviderInfo: Codable, Equatable, Sendable {
    public let providerInstanceId: String
    public let driverKind: String

    public init(providerInstanceId: String, driverKind: String) {
        self.providerInstanceId = providerInstanceId
        self.driverKind = driverKind
    }
}

/// Work item counts aggregated for a single generic agent tool.
public struct T3DriverCounts: Equatable, Sendable {
    public var runningTasks: Int = 0
    public var runningHelpers: Int = 0
    public var waitingTasks: Int = 0
    public var waitingHelpers: Int = 0
    public var startingTasks: Int = 0
    public var startingHelpers: Int = 0

    public init(
        runningTasks: Int = 0,
        runningHelpers: Int = 0,
        waitingTasks: Int = 0,
        waitingHelpers: Int = 0,
        startingTasks: Int = 0,
        startingHelpers: Int = 0
    ) {
        self.runningTasks = runningTasks
        self.runningHelpers = runningHelpers
        self.waitingTasks = waitingTasks
        self.waitingHelpers = waitingHelpers
        self.startingTasks = startingTasks
        self.startingHelpers = startingHelpers
    }

    public var activeScore: Int {
        runningTasks + runningHelpers
    }

    public var waitingScore: Int {
        waitingTasks + waitingHelpers
    }

    public var startingScore: Int {
        startingTasks + startingHelpers
    }

    public var totalWork: Int {
        activeScore + waitingScore + startingScore
    }
}

/// Snapshot of aggregated active work across all known tools at a point in time.
public struct T3ActivitySnapshot: Equatable, Sendable {
    public let observedAt: Date
    public let toolCounts: [String: T3DriverCounts]

    public init(observedAt: Date = Date(), toolCounts: [String: T3DriverCounts]) {
        self.observedAt = observedAt
        self.toolCounts = toolCounts
    }

    /// Snapshot TTL is 45 seconds per specification.
    public func isFresh(maxAgeSeconds: TimeInterval = 45.0, now: Date = Date()) -> Bool {
        now.timeIntervalSince(observedAt) >= 0 && now.timeIntervalSince(observedAt) <= maxAgeSeconds
    }
}

/// Fully formatted safe activity elements ready for Discord publication.
public struct T3FormattedActivity: Equatable, Sendable {
    public let details: String
    public let state: String?
    public let assets: DiscordActivityAssets?
    public let winningTool: String?

    public init(
        details: String,
        state: String? = nil,
        assets: DiscordActivityAssets? = nil,
        winningTool: String? = nil
    ) {
        self.details = details
        self.state = state
        self.assets = assets
        self.winningTool = winningTool
    }
}

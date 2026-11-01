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
            return "T3 work stays visible while the app is minimized."
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
/// Ignores prompts, files, paths, and URLs. `title` is kept only in memory for the local
/// pinned-task picker; it is never logged, persisted, or published.
public struct T3ThreadItem: Codable, Equatable, Sendable {
    public let threadId: String
    public let providerInstanceId: String
    public let relationshipToParent: String?
    public let status: String
    public let projectId: String?
    public let model: String?
    public let parentThreadId: String?
    public let createdAt: String?
    public let updatedAt: String?
    /// Local settings picker only.
    public let title: String?

    public init(
        threadId: String,
        providerInstanceId: String,
        relationshipToParent: String? = nil,
        status: String,
        projectId: String? = nil,
        model: String? = nil,
        parentThreadId: String? = nil,
        createdAt: String? = nil,
        updatedAt: String? = nil,
        title: String? = nil
    ) {
        self.threadId = threadId
        self.providerInstanceId = providerInstanceId
        self.relationshipToParent = relationshipToParent
        self.status = status
        self.projectId = projectId
        self.model = model
        self.parentThreadId = parentThreadId
        self.createdAt = createdAt
        self.updatedAt = updatedAt
        self.title = title
    }

    /// `relationshipToParent == "subagent"` means helper; fork or nil remains a task.
    public var isHelper: Bool {
        relationshipToParent == "subagent"
    }

    public var isTask: Bool {
        !isHelper
    }

    public var isUnfinished: Bool {
        T3ThreadStatus.isRunning(status) || T3ThreadStatus.isWaiting(status) || T3ThreadStatus.isStarting(status)
    }
}

/// Project metadata from `t3_project_list`. The title appears only in the local alias editor/picker.
public struct T3ProjectInfo: Codable, Equatable, Sendable {
    public let projectId: String
    /// Local settings picker only. Never published.
    public let title: String?

    public init(projectId: String, title: String? = nil) {
        self.projectId = projectId
        self.title = title
    }
}

/// Fresh result of polling one T3 environment.
public struct T3EnvironmentSnapshot: Equatable, Sendable {
    public let environmentId: String
    public let observedAt: Date
    public let providers: [T3ProviderInfo]
    /// Deduplicated within this environment.
    public let threads: [T3ThreadItem]
    public let projects: [T3ProjectInfo]

    public init(
        environmentId: String,
        observedAt: Date,
        providers: [T3ProviderInfo],
        threads: [T3ThreadItem],
        projects: [T3ProjectInfo] = []
    ) {
        self.environmentId = environmentId
        self.observedAt = observedAt
        self.providers = providers
        var seen = Set<String>()
        var unique: [T3ThreadItem] = []
        for thread in threads where !seen.contains(thread.threadId) {
            seen.insert(thread.threadId)
            unique.append(thread)
        }
        self.threads = unique
        self.projects = projects
    }

    public func isFresh(maxAgeSeconds: TimeInterval = 45.0, now: Date = Date()) -> Bool {
        let age = now.timeIntervalSince(observedAt)
        return age >= 0 && age <= maxAgeSeconds
    }
}

/// Combined snapshot across environments. Stale environments are dropped, not zeroed.
public struct T3CombinedSnapshot: Equatable, Sendable {
    public let environments: [T3EnvironmentSnapshot]

    public init(environments: [T3EnvironmentSnapshot]) {
        self.environments = environments
    }

    /// Only environments with a fresh snapshot.
    public func freshEnvironments(maxAgeSeconds: TimeInterval = 45.0, now: Date = Date()) -> [T3EnvironmentSnapshot] {
        environments.filter { $0.isFresh(maxAgeSeconds: maxAgeSeconds, now: now) }
    }

    /// Fresh only if at least one environment produced a fresh snapshot.
    public func isFresh(maxAgeSeconds: TimeInterval = 45.0, now: Date = Date()) -> Bool {
        !freshEnvironments(maxAgeSeconds: maxAgeSeconds, now: now).isEmpty
    }
}

/// A thread that survived exclusions, tagged with its environment and public tool name.
public struct T3WorkThread: Equatable, Sendable {
    public let environmentId: String
    public let thread: T3ThreadItem
    public let toolName: String

    public init(environmentId: String, thread: T3ThreadItem, toolName: String) {
        self.environmentId = environmentId
        self.thread = thread
        self.toolName = toolName
    }

    /// `"{environmentId}/{threadId}"`.
    public var threadKey: String { "\(environmentId)/\(thread.threadId)" }

    /// `"{environmentId}/{projectId}"`, or nil when the thread has no project.
    public var projectKey: String? {
        guard let projectId = thread.projectId, !projectId.isEmpty else { return nil }
        return "\(environmentId)/\(projectId)"
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

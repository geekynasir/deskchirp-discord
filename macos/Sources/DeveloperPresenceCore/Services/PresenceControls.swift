import Foundation

/// Work-session and shown-app clocks (spec/presence-v3.md §5.5).
public struct PresenceWorkClock: Equatable, Sendable {
    /// A work session ends after this long without unfinished work.
    public static let sessionGraceSeconds: TimeInterval = 120

    public private(set) var workSessionStart: Date?
    public private(set) var lastUnfinishedWorkAt: Date?
    public private(set) var shownApp: SupportedApp = .none
    public private(set) var appShownSince: Date?

    public init() {}

    /// Starts at the first fresh snapshot with unfinished work, survives running ↔ waiting ↔ starting,
    /// and ends after 120 seconds with no unfinished work or when activity sharing stops.
    public mutating func updateWork(hasUnfinishedWork: Bool, sharingActive: Bool, now: Date) {
        guard sharingActive else {
            workSessionStart = nil
            lastUnfinishedWorkAt = nil
            return
        }
        if hasUnfinishedWork {
            if workSessionStart == nil { workSessionStart = now }
            lastUnfinishedWorkAt = now
        } else if let last = lastUnfinishedWorkAt, now.timeIntervalSince(last) > Self.sessionGraceSeconds {
            workSessionStart = nil
            lastUnfinishedWorkAt = nil
        }
    }

    /// Restarts the app clock whenever the shown app changes.
    public mutating func updateShownApp(_ app: SupportedApp, now: Date) {
        guard app != shownApp else { return }
        shownApp = app
        appShownSince = app == .none ? nil : now
    }

    public var timerState: PresenceTimerState {
        PresenceTimerState(appShownSince: appShownSince, workSessionStart: workSessionStart)
    }
}

/// Which app the card describes and whether T3 rich data may appear (spec/presence-v3.md §4).
public enum PresenceSelection {
    public static func select(
        mode: PresenceSelectionMode,
        foreground: SupportedApp,
        t3Running: Bool,
        otherSupportedAppRunning: Bool,
        hasFreshT3Work: Bool
    ) -> (app: SupportedApp, richAllowed: Bool) {
        switch mode {
        case .keepBackgroundVisible:
            let showT3 = t3Running && (hasFreshT3Work || foreground == .none)
            return (showT3 ? .t3Code : foreground, true)
        case .followActiveApp:
            if foreground != .none { return (foreground, foreground == .t3Code) }
            if t3Running { return (.t3Code, !otherSupportedAppRunning) }
            return (.none, false)
        }
    }
}

/// Disabled-option reasons shown under settings controls (spec/presence-v3.md §3).
public enum PresenceAvailability {
    public static let connectReason = "Connect T3 Code to show work."
    public static let usagePermissionReason = "Allow token usage for an environment first."
    public static let contextScopeReason = "Context is measured per task."
    public static let pinTaskReason = "Pick a task to pin."
    public static let todayExclusionReason = "Today’s totals can’t skip excluded projects."
    public static let repoButtonReason = "Share a project name and add a public GitHub repository link first."

    public static func cardStyleReason(
        _ style: PresenceCardStyle,
        settings: PresenceSettings,
        anyEnvironmentConnected: Bool,
        anyUsageAllowed: Bool
    ) -> String? {
        switch style {
        case .appOnly:
            return nil
        case .workload:
            return settings.showT3Activity && anyEnvironmentConnected ? nil : connectReason
        case .usage:
            if !(settings.showT3Activity && anyEnvironmentConnected) { return connectReason }
            return anyUsageAllowed ? nil : usagePermissionReason
        }
    }

    public static func scopeReason(_ scope: PresenceUsageScope, settings: PresenceSettings) -> String? {
        if settings.usageMetric == .context && scope != .pinnedTask { return contextScopeReason }
        switch scope {
        case .today:
            return settings.excludedProjects.isEmpty ? nil : todayExclusionReason
        case .session:
            return nil
        case .pinnedTask:
            return settings.pinnedTask.isEmpty ? pinTaskReason : nil
        }
    }

    /// Estimated cost only applies to usage + tokens + today.
    public static func showsCostOption(_ settings: PresenceSettings) -> Bool {
        settings.cardStyle == .usage && settings.usageMetric == .tokens && settings.usageScope == .today
    }

    public static let pinnedProjectNotFoundText = "Pinned project not found"

    /// True when a pinned project is missing from every connected environment's project list.
    /// - Parameter catalogs: environment ID → project list, for connected environments whose list is known.
    ///   With no known list the answer is unknown, so this returns false.
    public static func pinnedProjectNotFound(_ pinnedProject: String, catalogs: [String: [T3ProjectInfo]]) -> Bool {
        guard !pinnedProject.isEmpty, !catalogs.isEmpty else { return false }
        return !catalogs.contains { environmentId, projects in
            projects.contains { "\(environmentId)/\($0.projectId)" == pinnedProject }
        }
    }

    public static func repoButtonReason(_ settings: PresenceSettings) -> String? {
        guard settings.shareProjectAlias else { return repoButtonReason }
        return PresenceCardFormatter.isValidPublicRepoUrl(settings.publicRepoUrl) ? nil : PresenceCardFormatter.invalidRepoText
    }
}

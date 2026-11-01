import Foundation

/// Clocks used for `timestamps.start` (spec/presence-v3.md §5.5).
public struct PresenceTimerState: Equatable, Sendable {
    /// When the shown app became the shown app.
    public var appShownSince: Date?
    /// Start of the current work session, or nil when there is none.
    public var workSessionStart: Date?

    public init(appShownSince: Date? = nil, workSessionStart: Date? = nil) {
        self.appShownSince = appShownSince
        self.workSessionStart = workSessionStart
    }
}

/// Where a preview value comes from. Shown next to each outgoing field in settings.
public enum PresencePreviewSource: String, Equatable, Sendable {
    case deskchirp = "Deskchirp"
    case t3Code = "T3 Code"
    case t3Usage = "T3 usage"
}

/// One outgoing field for the live preview.
public struct PresencePreviewRow: Equatable, Sendable, Identifiable {
    /// Stable field name: details, state, largeImage, largeText, smallImage, smallText, timestampsStart, button1, button2.
    public let field: String
    public let value: String
    public let source: PresencePreviewSource
    public var id: String { field }

    public init(field: String, value: String, source: PresencePreviewSource) {
        self.field = field
        self.value = value
        self.source = source
    }

    /// Plain-language name for the settings preview.
    public var label: String {
        switch field {
        case "details": return "First line"
        case "state": return "Second line"
        case "largeImage": return "Large image"
        case "largeText": return "Large image hover"
        case "smallImage": return "Small badge"
        case "smallText": return "Small badge hover"
        case "timestampsStart": return "Elapsed timer"
        case "button1": return "First button"
        case "button2": return "Second button"
        default: return field
        }
    }
}

/// Formatter output: the safe Discord activity (asset keys, before URL resolution) plus preview data.
public struct PresenceCard: Equatable, Sendable {
    public let activity: DiscordActivity?
    public let previewRows: [PresencePreviewRow]
    /// Field names (`details`, `state`) longer than the soft fit limit.
    public let fitWarnings: [String]
    /// Featured tool, when a rich card was produced.
    public let featuredTool: String?
    /// `"{environmentId}/{projectId}"` whose alias was considered, for tie stability.
    public let featuredProjectKey: String?
    /// True when fresh unfinished work backed the card.
    public let hasFreshWork: Bool
    /// True when the state line shows a usage value.
    public let showsUsage: Bool

    public init(
        activity: DiscordActivity?,
        previewRows: [PresencePreviewRow] = [],
        fitWarnings: [String] = [],
        featuredTool: String? = nil,
        featuredProjectKey: String? = nil,
        hasFreshWork: Bool = false,
        showsUsage: Bool = false
    ) {
        self.activity = activity
        self.previewRows = previewRows
        self.fitWarnings = fitWarnings
        self.featuredTool = featuredTool
        self.featuredProjectKey = featuredProjectKey
        self.hasFreshWork = hasFreshWork
        self.showsUsage = showsUsage
    }
}

/// Pure card formatter for presence controls v3 (spec/presence-v3.md §5).
/// Only allowlisted strings, counts, sanitized aliases/models, and fixed URLs ever reach the output.
public enum PresenceCardFormatter {
    public static let hardCap = 128
    public static let softFit = 34
    public static let fitWarningText = "May be cut off on Discord. Try a shorter alias or turn off a detail."
    public static let companionButton = DiscordActivityButton(
        label: "Get Deskchirp", url: "https://github.com/geekynasir/deskchirp-discord")
    public static let repoButtonLabel = "View project"
    public static let invalidRepoText = "Use a public GitHub repository link."
    public static let publicToolNames: Set<String> = [
        "Claude Code", "Codex", "Antigravity", "Cursor", "OpenCode", "Grok", "Muse", "Pi", "Other tools"
    ]

    private enum Phase { case running, waiting, starting }

    /// - Parameters:
    ///   - featuredTool: winner from the coordinator's hysteresis selector; nil computes it statelessly.
    ///   - previousProjectKey: the previously featured project, so ties keep it.
    ///   - richAllowed: false when the selection mode allows only the app-only card for T3.
    public static func format(
        settings: PresenceSettings,
        snapshot: T3CombinedSnapshot?,
        usage: T3UsageSnapshot?,
        timer: PresenceTimerState,
        shownApp: SupportedApp,
        now: Date,
        featuredTool: String? = nil,
        previousProjectKey: String? = nil,
        richAllowed: Bool = true,
        snapshotTTLSeconds: TimeInterval = 45.0
    ) -> PresenceCard {
        let buttonsForAppCard = buttons(settings: settings, aliasShown: false)

        switch shownApp {
        case .none:
            return PresenceCard(activity: nil)
        case .claude, .codex:
            // Other desktop apps keep app-name-only presence; T3 data never appears here.
            let activity = DiscordActivity(
                details: shownApp.presenceDetails ?? "",
                timestamps: timestamp(timer.appShownSince, settings: settings),
                buttons: buttonsForAppCard)
            return finish(activity: activity, detailsSource: .deskchirp, stateSource: .deskchirp,
                          badgeSource: .deskchirp, featuredTool: nil, projectKey: nil,
                          hasFreshWork: false, showsUsage: false)
        case .t3Code:
            break
        }

        let appOnlyAssets = settings.showAppIcons ? DiscordActivityAssets(largeImage: "t3_code", largeText: "T3 Code") : nil
        let appOnly = DiscordActivity(
            details: "Using T3 Code",
            assets: appOnlyAssets,
            timestamps: timestamp(timer.appShownSince, settings: settings),
            buttons: buttonsForAppCard)
        let appOnlyCard = finish(activity: appOnly, detailsSource: .deskchirp, stateSource: .deskchirp,
                                 badgeSource: .deskchirp, featuredTool: nil, projectKey: nil,
                                 hasFreshWork: false, showsUsage: false)

        guard richAllowed, settings.showT3Activity, settings.cardStyle != .appOnly,
              let snapshot = snapshot, snapshot.isFresh(maxAgeSeconds: snapshotTTLSeconds, now: now) else {
            return appOnlyCard
        }

        let load = self.workload(settings: settings, snapshot: snapshot, now: now, snapshotTTLSeconds: snapshotTTLSeconds)
        let work = load.work
        let counts = load.counts

        let tool: String
        if let preferred = featuredTool, publicToolNames.contains(preferred), (counts[preferred]?.totalWork ?? 0) > 0 {
            tool = preferred
        } else if let best = T3WinnerSelector.bestTool(toolCounts: counts, previous: featuredTool),
                  publicToolNames.contains(best) {
            tool = best
        } else {
            return appOnlyCard
        }
        guard let toolCounts = counts[tool], toolCounts.totalWork > 0 else { return appOnlyCard }

        let phase: Phase
        if toolCounts.activeScore > 0 { phase = .running }
        else if toolCounts.waitingScore > 0 { phase = .waiting }
        else { phase = .starting }

        // Details: alias (if shared and available) or the public tool name.
        let projectKey: String?
        if !settings.pinnedProject.isEmpty {
            projectKey = settings.pinnedProject
        } else {
            projectKey = T3WorkAggregator.featuredProject(work, tool: tool, previous: previousProjectKey)
        }
        var alias: String?
        if settings.shareProjectAlias, let key = projectKey, let raw = settings.projectAliases[key] {
            alias = cleanAlias(raw)
        }
        let details = alias.map { "T3 Code · \($0)" } ?? "T3 Code · \(tool)"

        // State: workload, optionally replaced by a usage value.
        var workloadState = workloadText(counts: toolCounts, phase: phase, playful: settings.playfulWording)
        if settings.shareModel, let model = modelLabel(work: work, tool: tool, pinnedTask: settings.pinnedTask) {
            workloadState += " · \(model)"
        }
        var state = workloadState
        var stateSource = PresencePreviewSource.t3Code
        var largeText = "T3 Code"
        var showsUsage = false
        if settings.cardStyle == .usage,
           let usageLine = usageText(
               settings: settings, usage: usage, now: now,
               pinnedTaskVisible: T3UsageService.isPinnedTaskVisible(settings.pinnedTask, work: load.visible),
               workSessionStart: timer.workSessionStart) {
            state = usageLine.text
            stateSource = .t3Usage
            largeText = "T3 Code · \(usageLine.definition)"
            showsUsage = true
        }

        // Assets.
        var assets: DiscordActivityAssets?
        if settings.showAppIcons {
            var smallImage: String?
            var smallText: String?
            switch settings.smallBadge {
            case .status:
                switch phase {
                case .running: smallImage = "status_working"; smallText = "Working"
                case .waiting: smallImage = "status_attention"; smallText = "Waiting"
                case .starting: smallImage = "status_working"; smallText = "Starting"
                }
            case .provider:
                if let key = assetKeyForTool(tool) { smallImage = key; smallText = tool }
            case .hidden:
                break
            }
            assets = DiscordActivityAssets(
                largeImage: "t3_code", largeText: cap(largeText),
                smallImage: smallImage, smallText: smallText.map { cap($0) })
        }

        let start = timestamp(timer.workSessionStart ?? timer.appShownSince, settings: settings)
        let activity = DiscordActivity(
            details: details,
            state: state,
            assets: assets,
            timestamps: start,
            buttons: buttons(settings: settings, aliasShown: alias != nil))
        return finish(activity: activity,
                      detailsSource: alias != nil ? .deskchirp : .t3Code,
                      stateSource: stateSource,
                      badgeSource: settings.smallBadge == .hidden ? .deskchirp : .t3Code,
                      featuredTool: tool, projectKey: projectKey,
                      hasFreshWork: true, showsUsage: showsUsage)
    }

    // MARK: - Pieces

    /// Work threads after exclusions and the pinned-project restriction, with per-tool counts.
    /// The coordinator feeds the same counts to its hysteresis selector.
    public static func workload(
        settings: PresenceSettings,
        snapshot: T3CombinedSnapshot?,
        now: Date,
        snapshotTTLSeconds: TimeInterval = 45.0
    ) -> (work: [T3WorkThread], counts: [String: T3DriverCounts], pinnedProjectPresent: Bool, visible: [T3WorkThread]) {
        guard let snapshot = snapshot else { return ([], [:], false, []) }
        let allWork = T3WorkAggregator.workThreads(
            snapshot: snapshot, excludedProjects: settings.excludedProjects,
            maxAgeSeconds: snapshotTTLSeconds, now: now)
        // A pinned project always restricts the counts; no matching unfinished work → no fresh work.
        let pinnedPresent = isPinnedProjectPresent(settings.pinnedProject, work: allWork, snapshot: snapshot)
        let work = settings.pinnedProject.isEmpty ? allWork : allWork.filter { $0.projectKey == settings.pinnedProject }
        return (work, T3WorkAggregator.toolCounts(work), pinnedPresent, allWork)
    }

    private static func isPinnedProjectPresent(_ pinned: String, work: [T3WorkThread], snapshot: T3CombinedSnapshot) -> Bool {
        guard !pinned.isEmpty else { return false }
        if work.contains(where: { $0.projectKey == pinned }) { return true }
        return snapshot.environments.contains { environment in
            environment.projects.contains { "\(environment.environmentId)/\($0.projectId)" == pinned }
        }
    }

    /// Remove control characters, collapse whitespace, trim, keep 32 characters, trim again.
    public static func cleanAlias(_ raw: String) -> String? {
        var scalars = String.UnicodeScalarView()
        var lastWasSpace = false
        for scalar in raw.unicodeScalars {
            if CharacterSet.whitespacesAndNewlines.contains(scalar) {
                if !lastWasSpace { scalars.append(" ") }
                lastWasSpace = true
                continue
            }
            if scalar.properties.generalCategory == .control || scalar.properties.generalCategory == .format {
                continue
            }
            scalars.append(scalar)
            lastWasSpace = false
        }
        let collapsed = String(scalars).trimmingCharacters(in: .whitespaces)
        let capped = String(collapsed.prefix(32)).trimmingCharacters(in: .whitespaces)
        return capped.isEmpty ? nil : capped
    }

    /// Accept only `[A-Za-z0-9 ._:/-]`, at most 40 characters.
    public static func sanitizeModel(_ raw: String?) -> String? {
        guard let raw = raw else { return nil }
        let trimmed = raw.trimmingCharacters(in: .whitespaces)
        guard !trimmed.isEmpty, trimmed.count <= 40 else { return nil }
        let allowed = trimmed.unicodeScalars.allSatisfy { scalar in
            switch scalar.value {
            case 0x30...0x39, 0x41...0x5A, 0x61...0x7A: return true
            case 0x20, 0x2E, 0x5F, 0x3A, 0x2F, 0x2D: return true
            default: return false
            }
        }
        return allowed ? trimmed : nil
    }

    /// Model of the pinned task when present, else the featured tool's most recently updated running task.
    static func modelLabel(work: [T3WorkThread], tool: String, pinnedTask: String) -> String? {
        if !pinnedTask.isEmpty, let pinned = work.first(where: { $0.threadKey == pinnedTask }) {
            return sanitizeModel(pinned.thread.model)
        }
        let candidates = work.filter {
            $0.toolName == tool && $0.thread.isTask && T3ThreadStatus.isRunning($0.thread.status)
        }
        var best: T3WorkThread?
        var bestDate = Date.distantPast
        for candidate in candidates {
            let date = parseDate(candidate.thread.updatedAt) ?? Date.distantPast
            if best == nil || date > bestDate {
                best = candidate
                bestDate = date
            }
        }
        return sanitizeModel(best?.thread.model)
    }

    static func parseDate(_ value: String?) -> Date? {
        guard let value = value else { return nil }
        let plain = ISO8601DateFormatter()
        if let date = plain.date(from: value) { return date }
        let fractional = ISO8601DateFormatter()
        fractional.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return fractional.date(from: value)
    }

    private static func plural(_ n: Int, _ word: String) -> String {
        "\(n) \(word)\(n == 1 ? "" : "s")"
    }

    private static func workloadText(counts: T3DriverCounts, phase: Phase, playful: Bool) -> String {
        switch phase {
        case .running:
            if playful { return "Herding \(plural(counts.activeScore, "agent"))" }
            return joined(counts.runningTasks, counts.runningHelpers, suffix: "")
        case .waiting:
            if playful { return "\(plural(counts.waitingScore, "agent")) on standby" }
            return joined(counts.waitingTasks, counts.waitingHelpers, suffix: " waiting")
        case .starting:
            if playful { return "Warming up \(plural(counts.startingScore, "agent"))" }
            return joined(counts.startingTasks, counts.startingHelpers, suffix: " starting")
        }
    }

    private static func joined(_ tasks: Int, _ helpers: Int, suffix: String) -> String {
        var parts: [String] = []
        if tasks > 0 { parts.append(plural(tasks, "task") + suffix) }
        if helpers > 0 { parts.append(plural(helpers, "helper") + suffix) }
        return parts.joined(separator: " · ")
    }

    /// Effective scope: context is measured per task.
    public static func effectiveScope(_ settings: PresenceSettings) -> PresenceUsageScope {
        settings.usageMetric == .context ? .pinnedTask : settings.usageScope
    }

    public static func usageDefinition(metric: PresenceUsageMetric, scope: PresenceUsageScope) -> String {
        if metric == .context { return "How full the pinned task's context window is" }
        switch scope {
        case .today: return "Tokens counted today across providers, including use outside T3"
        case .session: return "Tokens counted since this work session started"
        case .pinnedTask: return "Tokens counted for the pinned task"
        }
    }

    /// The usage state line, or nil when the value is unavailable, stale, partial, or would use excluded data.
    /// - Parameters:
    ///   - pinnedTaskVisible: the pinned task is in the exclusion-filtered snapshot.
    ///   - workSessionStart: the current work session; session usage from another session is never shown.
    static func usageText(
        settings: PresenceSettings,
        usage: T3UsageSnapshot?,
        now: Date,
        pinnedTaskVisible: Bool,
        workSessionStart: Date?
    ) -> (text: String, definition: String)? {
        guard let usage = usage, usage.isFresh(now: now) else { return nil }
        let scope = effectiveScope(settings)
        let value: T3UsageValue?
        switch scope {
        case .today:
            // The provider aggregate cannot honor exclusions.
            guard settings.excludedProjects.isEmpty else { return nil }
            value = usage.today
        case .session:
            guard let current = workSessionStart, let measured = usage.sessionStart,
                  abs(current.timeIntervalSince(measured)) < 1 else { return nil }
            value = usage.session
        case .pinnedTask:
            // An excluded (or helper-of-excluded) or vanished pinned task never shows usage.
            guard !settings.pinnedTask.isEmpty, pinnedTaskVisible else { return nil }
            if let key = usage.pinnedTaskKey, key != settings.pinnedTask { return nil }
            value = usage.pinnedTask
        }
        guard let value = value else { return nil }
        let definition = usageDefinition(metric: settings.usageMetric, scope: scope)

        switch settings.usageMetric {
        case .context:
            guard let percent = value.contextPercent else { return nil }
            let text = settings.playfulWording ? "Context tank \(percent)% full" : "\(percent)% of context used"
            return (text, definition)
        case .tokens:
            guard let tokens = value.tokens, !value.tokensPartial else { return nil }
            let amount = tokens.value(for: settings.tokenCategory)
            guard amount > 0 else { return nil }
            let formatted = PresenceNumberFormat.tokens(amount, rounding: settings.tokenRounding)
            let scopeText: String
            switch scope {
            case .today: scopeText = "today"
            case .session: scopeText = "this session"
            case .pinnedTask: scopeText = "on this task"
            }
            let noun = amount == 1 ? "token" : "tokens"
            var text = settings.playfulWording
                ? "Crunched \(formatted) \(noun) \(scopeText)"
                : "\(formatted) \(noun) \(scopeText)"
            if settings.shareEstimatedCost, scope == .today, !value.costPartial,
               let cost = value.costUsd, let costText = PresenceNumberFormat.cost(cost) {
                text += " · \(costText) API est."
            }
            return (text, definition)
        }
    }

    static func assetKeyForTool(_ tool: String) -> String? {
        switch tool {
        case "Claude Code": return "harness_claude"
        case "Codex": return "harness_codex"
        case "Antigravity": return "harness_antigravity"
        case "Cursor": return "harness_cursor"
        case "OpenCode": return "harness_opencode"
        case "Grok": return "harness_grok"
        default: return nil
        }
    }

    /// `https://github.com/{owner}/{repo}` only, with no query, fragment, or extra path.
    public static func isValidPublicRepoUrl(_ raw: String) -> Bool {
        let prefix = "https://github.com/"
        guard raw.hasPrefix(prefix) else { return false }
        let rest = raw.dropFirst(prefix.count)
        let parts = rest.split(separator: "/", omittingEmptySubsequences: false)
        guard parts.count == 2 else { return false }
        let owner = parts[0], repo = parts[1]
        guard (1...39).contains(owner.count), (1...100).contains(repo.count), repo != ".", repo != ".." else { return false }
        let ownerOK = owner.unicodeScalars.allSatisfy { isAlnum($0) || $0 == "-" }
        let repoOK = repo.unicodeScalars.allSatisfy { isAlnum($0) || $0 == "." || $0 == "_" || $0 == "-" }
        return ownerOK && repoOK
    }

    private static func isAlnum(_ scalar: Unicode.Scalar) -> Bool {
        switch scalar.value {
        case 0x30...0x39, 0x41...0x5A, 0x61...0x7A: return true
        default: return false
        }
    }

    static func buttons(settings: PresenceSettings, aliasShown: Bool) -> [DiscordActivityButton]? {
        switch settings.buttons {
        case .hidden:
            return nil
        case .companion:
            return [companionButton]
        case .companionAndRepo:
            var list = [companionButton]
            if aliasShown, settings.shareProjectAlias, isValidPublicRepoUrl(settings.publicRepoUrl) {
                list.append(DiscordActivityButton(label: repoButtonLabel, url: settings.publicRepoUrl))
            }
            return Array(list.prefix(2))
        }
    }

    private static func timestamp(_ date: Date?, settings: PresenceSettings) -> DiscordActivityTimestamps? {
        guard settings.showElapsedTimer, let date = date else { return nil }
        return DiscordActivityTimestamps(start: Int64(date.timeIntervalSince1970.rounded(.down)))
    }

    /// Hard cap: 128 characters, ending in `…` when cut.
    public static func cap(_ text: String) -> String {
        guard text.count > hardCap else { return text }
        return String(text.prefix(hardCap - 1)) + "…"
    }

    private static func finish(
        activity: DiscordActivity,
        detailsSource: PresencePreviewSource,
        stateSource: PresencePreviewSource,
        badgeSource: PresencePreviewSource,
        featuredTool: String?,
        projectKey: String?,
        hasFreshWork: Bool,
        showsUsage: Bool
    ) -> PresenceCard {
        let details = cap(activity.details)
        let state = activity.state.map { cap($0) }
        let capped = DiscordActivity(
            details: details, state: state, assets: activity.assets,
            timestamps: activity.timestamps, buttons: activity.buttons)

        var rows: [PresencePreviewRow] = [PresencePreviewRow(field: "details", value: details, source: detailsSource)]
        if let state = state { rows.append(PresencePreviewRow(field: "state", value: state, source: stateSource)) }
        if let assets = activity.assets {
            if let value = assets.large_image { rows.append(PresencePreviewRow(field: "largeImage", value: value, source: .deskchirp)) }
            if let value = assets.large_text {
                rows.append(PresencePreviewRow(field: "largeText", value: value, source: showsUsage ? .t3Usage : .deskchirp))
            }
            if let value = assets.small_image { rows.append(PresencePreviewRow(field: "smallImage", value: value, source: badgeSource)) }
            if let value = assets.small_text { rows.append(PresencePreviewRow(field: "smallText", value: value, source: badgeSource)) }
        }
        if let start = activity.timestamps?.start {
            rows.append(PresencePreviewRow(field: "timestampsStart", value: String(start), source: .deskchirp))
        }
        for (index, button) in (activity.buttons ?? []).enumerated() {
            rows.append(PresencePreviewRow(field: "button\(index + 1)", value: "\(button.label) → \(button.url)", source: .deskchirp))
        }

        var warnings: [String] = []
        if details.count > softFit { warnings.append("details") }
        if let state = state, state.count > softFit { warnings.append("state") }

        return PresenceCard(
            activity: capped, previewRows: rows, fitWarnings: warnings,
            featuredTool: featuredTool, featuredProjectKey: projectKey,
            hasFreshWork: hasFreshWork, showsUsage: showsUsage)
    }
}

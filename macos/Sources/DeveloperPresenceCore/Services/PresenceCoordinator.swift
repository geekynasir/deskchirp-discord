import Foundation
import Combine
#if canImport(AppKit)
import AppKit
#endif

/// Main coordinator managing app lifecycle, foreground detection, session events,
/// T3 Code activity polling, privacy policy enforcement, and Discord IPC.
@MainActor
public final class PresenceCoordinator: ObservableObject {
    @Published public private(set) var state: PresenceState
    @Published public private(set) var settings: PresenceSettings
    @Published public private(set) var menuBarIconName: String = "circle"

    private let settingsService: SettingsServicing
    private let foregroundDetector: ForegroundDetecting
    private let sessionObserver: SessionStateObserving
    private let discordClient: DiscordIpcClient
    /// Injected service; serves the first environment and is the template for the others.
    private let primaryT3Service: T3ActivityService
    /// One activity service per environment ID.
    private var t3Services: [String: T3ActivityService] = [:]
    /// The first environment's service (legacy single-environment API).
    public var t3Service: T3ActivityService {
        if let first = settings.t3Environments.first, let service = t3Services[first.id] { return service }
        return primaryT3Service
    }
    public let usageService: T3UsageService
    public let computers: ComputerLinkService

    // MARK: Presence controls v3 state
    @Published public private(set) var environmentStatuses: [String: T3ConnectionStatus] = [:]
    @Published public private(set) var environmentDetails: [String: String] = [:]
    @Published public private(set) var usageStatuses: [String: T3UsagePermissionStatus] = [:]
    @Published public private(set) var usageSnapshot: T3UsageSnapshot?
    /// Exact outgoing card with preview rows and fit warnings.
    @Published public private(set) var previewCard = PresenceCard(activity: nil)
    @Published public private(set) var environmentMessage: String?
    private var workClock = PresenceWorkClock()
    private let combinedSelector = T3WinnerSelector()
    private var lastFeaturedProject: String?
    private var usageTimer: Timer?
    private var usagePollTask: Task<Void, Never>?
    private var usageGeneration = 0
    private var usagePollInFlight = false
    private var computerTimer: Timer?
    private var discordUserId: String?

    @Published public private(set) var isFindingT3 = false
    @Published public private(set) var t3SetupMessage: String?
    private var t3SetupTask: Task<Void, Never>?
    private let t3Discovery: T3LocalDiscovery

    private var started = false

    public init(
        settingsService: SettingsServicing = SettingsService.shared,
        foregroundDetector: ForegroundDetecting? = nil,
        sessionObserver: SessionStateObserving? = nil,
        discordClient: DiscordIpcClient? = nil,
        t3Service: T3ActivityService = T3ActivityService(),
        computers: ComputerLinkService? = nil,
        t3Discovery: T3LocalDiscovery = T3LocalDiscovery(),
        usageService: T3UsageService = T3UsageService()
    ) {
        self.settingsService = settingsService
        self.t3Discovery = t3Discovery
        let initialSettings = settingsService.loadSettings()
        self.foregroundDetector = foregroundDetector ?? WorkspaceForegroundDetector(bundleMappings: initialSettings.bundleMappings)
        self.sessionObserver = sessionObserver ?? WorkspaceSessionObserver()
        self.discordClient = discordClient ?? DiscordIpcClient()
        self.primaryT3Service = t3Service
        self.usageService = usageService
        self.computers = computers ?? ComputerLinkService()

        self.settings = initialSettings
        self.state = PresenceState()
        self.state.isSessionSuspended = self.sessionObserver.isSuspended

        setupBindings()

        // Restore each saved environment's connection from secure storage.
        for environment in initialSettings.t3Environments {
            activityService(for: environment).restoreSavedConnection(address: environment.address)
        }
        refreshUsageStatuses()
    }

    // MARK: - Environments

    /// Returns (creating if needed) the activity service for an environment.
    private func activityService(for environment: T3Environment) -> T3ActivityService {
        if let existing = t3Services[environment.id] { return existing }
        let primaryInUse = t3Services.values.contains { $0 === primaryT3Service }
        let service = primaryInUse ? primaryT3Service.makeSibling() : primaryT3Service
        t3Services[environment.id] = service
        bind(service: service, environmentId: environment.id)
        return service
    }

    private func bind(service: T3ActivityService, environmentId: String) {
        service.onStatusChanged = { [weak self, weak service] status, message in
            Task { @MainActor [weak self, weak service] in
                guard let self = self, let service = service, self.t3Services[environmentId] === service else { return }
                self.environmentStatuses[environmentId] = status
                self.environmentDetails[environmentId] = UserFacingStatus.t3Detail(status: status, message: message ?? status.recoveryText)
                self.refreshAggregateT3Status(message: message)
                self.evaluatePresence(immediate: false)
            }
        }
        service.onActivityUpdated = { [weak self, weak service] _, _ in
            Task { @MainActor [weak self, weak service] in
                guard let self = self, self.started, let service = service,
                      self.t3Services[environmentId] === service else { return }
                self.evaluatePresence(immediate: false)
            }
        }
    }

    /// Summarises environment statuses into the single status line.
    private func refreshAggregateT3Status(message: String?) {
        let environments = settings.t3Environments
        let statuses = environments.map { environmentStatuses[$0.id] ?? .notConnected }
        let aggregate: T3ConnectionStatus
        if statuses.contains(.connected) { aggregate = .connected }
        else if statuses.contains(.openingSignIn) { aggregate = .openingSignIn }
        else if statuses.contains(.signInAgain) { aggregate = .signInAgain }
        else if statuses.contains(.couldNotReach) { aggregate = .couldNotReach }
        else { aggregate = .notConnected }
        state.t3ConnectionStatus = aggregate

        if environments.count > 1 {
            let connected = statuses.filter { $0 == .connected }.count
            state.t3StatusDetail = "Connected to \(connected) of \(environments.count) environments"
        } else {
            state.t3StatusDetail = message ?? aggregate.recoveryText
        }
        if aggregate == .couldNotReach || aggregate == .signInAgain {
            state.t3LastError = message
        } else {
            state.t3LastError = nil
        }
    }

    /// Per-environment recovery text, including the hint for an environment that never returned work.
    public func environmentDetail(_ id: String) -> String {
        let status = environmentStatus(id)
        if status == .connected, let service = t3Services[id], service.currentSnapshot != nil, !service.hasEverReturnedWork {
            return Self.noWorkHereText
        }
        return environmentDetails[id] ?? status.recoveryText
    }

    public static let noWorkHereText = "Connected. No running tasks were found here — add the environment where your tasks run."

    public func environmentStatus(_ id: String) -> T3ConnectionStatus {
        environmentStatuses[id] ?? .notConnected
    }

    /// **Add environment**: accepts a Copy MCP URL value or an origin.
    @discardableResult
    public func addEnvironment(address: String, label: String? = nil) -> Bool {
        environmentMessage = nil
        let environment: T3Environment
        do {
            environment = try T3Environment.make(fromAddress: address, label: label)
        } catch {
            environmentMessage = error.localizedDescription
            return false
        }
        let origin = environment.endpoint?.origin
        if settings.t3Environments.contains(where: { $0.endpoint?.origin == origin }) {
            environmentMessage = "That environment is already in the list."
            return false
        }
        settings.t3Environments.append(environment)
        syncLegacyAddress()
        settingsService.saveSettings(settings)
        activityService(for: environment).handleEndpointChange(address: environment.address)
        refreshUsageStatuses()
        evaluatePresence(immediate: false)
        return true
    }

    /// **Remove**: deletes the environment's sign-in and token access, and its pins.
    public func removeEnvironment(id: String) {
        guard let environment = settings.t3Environments.first(where: { $0.id == id }) else { return }
        if let service = t3Services[id] {
            service.updateEligibility(isEligibleToPoll: false)
            service.disconnect()
            t3Services.removeValue(forKey: id)
            service.onStatusChanged = nil
            service.onActivityUpdated = nil
        }
        if let origin = environment.endpoint?.origin {
            try? usageService.removeAccess(environmentId: id, origin: origin)
        }
        settings.t3Environments.removeAll { $0.id == id }
        let prefix = id + "/"
        if settings.pinnedTask.hasPrefix(prefix) { settings.pinnedTask = "" }
        if settings.pinnedProject.hasPrefix(prefix) { settings.pinnedProject = "" }
        settings.excludedProjects = settings.excludedProjects.filter { !$0.hasPrefix(prefix) }
        settings.projectAliases = settings.projectAliases.filter { !$0.key.hasPrefix(prefix) }
        environmentStatuses.removeValue(forKey: id)
        environmentDetails.removeValue(forKey: id)
        usageStatuses.removeValue(forKey: id)
        syncLegacyAddress()
        settingsService.saveSettings(settings)
        refreshAggregateT3Status(message: nil)
        restartUsagePolling()
        evaluatePresence(immediate: true)
    }

    public func renameEnvironment(id: String, label: String) {
        guard let index = settings.t3Environments.firstIndex(where: { $0.id == id }) else { return }
        let trimmed = label.trimmingCharacters(in: .whitespacesAndNewlines)
        settings.t3Environments[index].label = String(trimmed.prefix(64))
        settingsService.saveSettings(settings)
    }

    /// **Allow** for one environment: opens T3's own sign-in in the browser.
    public func connectEnvironment(id: String) {
        guard let environment = settings.t3Environments.first(where: { $0.id == id }),
              environmentStatus(id) != .openingSignIn, !isFindingT3 else { return }
        let service = activityService(for: environment)
        t3SetupTask?.cancel()
        t3SetupMessage = nil
        t3SetupTask = Task {
            do {
                try await service.connect(address: environment.address)
                try Task.checkCancellation()
                if service.connectionStatus == .connected && !settings.showT3Activity {
                    settings.showT3Activity = true
                    settingsService.saveSettings(settings)
                }
                evaluatePresence(immediate: true)
            } catch {
                guard !Task.isCancelled else { return }
                evaluatePresence(immediate: false)
            }
        }
    }

    public func disconnectEnvironment(id: String) {
        guard let service = t3Services[id] else { return }
        service.disconnect()
        evaluatePresence(immediate: true)
    }

    /// Mirrors the first environment into the legacy `t3Address` for older builds.
    private func syncLegacyAddress() {
        settings.t3Address = settings.t3Environments.first?.address ?? ""
    }

    // MARK: - Token usage permission

    public func refreshUsageStatuses() {
        var result: [String: T3UsagePermissionStatus] = [:]
        for environment in settings.t3Environments {
            guard let origin = environment.endpoint?.origin else { continue }
            result[environment.id] = usageService.status(environmentId: environment.id, origin: origin)
        }
        usageStatuses = result
    }

    public func usageStatus(_ id: String) -> T3UsagePermissionStatus {
        usageStatuses[id] ?? .notAllowed
    }

    public var anyUsageAllowed: Bool {
        settings.t3Environments.contains { usageStatus($0.id).isAllowed }
    }

    public var anyEnvironmentConnected: Bool {
        settings.t3Environments.contains { environmentStatus($0.id) == .connected }
    }

    /// Pastes a pairing code or pairing link for an environment.
    public func pairUsage(environmentId: String, input: String) {
        guard let environment = settings.t3Environments.first(where: { $0.id == environmentId }),
              let origin = environment.endpoint?.origin else { return }
        usageStatuses[environmentId] = .pairing
        let usageService = self.usageService
        Task {
            do {
                try await usageService.pair(environmentId: environmentId, origin: origin, input: input)
            } catch {
                // The service keeps a plain-language failure state; server text is never shown.
            }
            refreshUsageStatuses()
            restartUsagePolling()
            evaluatePresence(immediate: false)
        }
    }

    /// **Remove token access**.
    public func removeUsageAccess(environmentId: String) {
        guard let environment = settings.t3Environments.first(where: { $0.id == environmentId }),
              let origin = environment.endpoint?.origin else { return }
        try? usageService.removeAccess(environmentId: environmentId, origin: origin)
        refreshUsageStatuses()
        restartUsagePolling()
        evaluatePresence(immediate: true)
    }

    // MARK: - Local pickers (titles stay in memory and on this computer)

    public struct ProjectChoice: Identifiable, Equatable {
        public let id: String
        public let title: String
        public let environmentLabel: String
    }

    public struct TaskChoice: Identifiable, Equatable {
        public let id: String
        public let title: String
        public let environmentLabel: String
    }

    /// Projects with their local titles, for the alias editor, exclusions and pinned project.
    public var projectChoices: [ProjectChoice] {
        var choices: [ProjectChoice] = []
        for environment in settings.t3Environments {
            guard let service = t3Services[environment.id] else { continue }
            for project in service.latestProjects {
                let title = project.title?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
                choices.append(ProjectChoice(
                    id: "\(environment.id)/\(project.projectId)",
                    title: title.isEmpty ? "Untitled project" : title,
                    environmentLabel: environment.label))
            }
        }
        return choices
    }

    /// Top-level tasks, newest first, for the pinned-task picker.
    public var taskChoices: [TaskChoice] {
        var rows: [(TaskChoice, String)] = []
        for environment in settings.t3Environments {
            guard let snapshot = t3Services[environment.id]?.environmentSnapshot(environmentId: environment.id) else { continue }
            for thread in snapshot.threads where thread.isTask {
                let title = thread.title?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
                rows.append((TaskChoice(
                    id: "\(environment.id)/\(thread.threadId)",
                    title: title.isEmpty ? "Untitled task" : title,
                    environmentLabel: environment.label), thread.updatedAt ?? thread.createdAt ?? ""))
            }
        }
        return rows.sorted { $0.1 > $1.1 }.map { $0.0 }
    }

    // MARK: - Settings updates

    /// Applies a change to the v3 presence controls, normalizes dependent options, saves, and republishes.
    public func updateControls(_ change: (inout PresenceSettings) -> Void) {
        var updated = settings
        change(&updated)
        if updated.usageMetric == .context { updated.usageScope = .pinnedTask }
        updated.projectAliases = updated.projectAliases.filter { !$0.value.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }
        let usageInputsChanged = updated.cardStyle != settings.cardStyle || updated.usageScope != settings.usageScope
            || updated.usageMetric != settings.usageMetric || updated.pinnedTask != settings.pinnedTask
            || updated.excludedProjects != settings.excludedProjects || updated.pinnedProject != settings.pinnedProject
        settings = updated
        settingsService.saveSettings(settings)
        if usageInputsChanged { restartUsagePolling() }
        evaluatePresence(immediate: true)
    }

    private func setupBindings() {
        let permission = computers.permission
        discordClient.publishAllowed = { account in permission.allows(account: account) }
        discordClient.onAccountChanged = { [weak self] account in
            Task { @MainActor [weak self] in
                guard let self = self, self.started else { return }
                self.discordUserId = account
                self.evaluatePresence()
            }
        }
        computers.onChanged = { [weak self] in
            guard let self = self, self.started else { return }
            self.state.computerStatus = self.computers.status
            self.evaluatePresence()
        }
        foregroundDetector.onForegroundAppChanged = { [weak self] app, bundleId in
            Task { @MainActor [weak self] in
                guard let self = self, self.started else { return }
                self.state.foregroundApp = app
                self.state.detectedBundleId = bundleId
                self.evaluatePresence(immediate: false)
            }
        }

        sessionObserver.onSessionStateChanged = { [weak self] isSuspended in
            Task { @MainActor [weak self] in
                guard let self = self, self.started else { return }
                self.state.isSessionSuspended = isSuspended
                if isSuspended {
                    self.computers.updateContext(linked: self.settings.followComputers, available: false, discordUserId: nil)
                    // Privacy rule: immediate clear on sleep or lock
                    self.setAllEligibility(false)
                    self.stopUsagePolling(clear: true)
                    self.discordClient.clearAndCloseImmediately()
                    self.state.connectionStatus = .disconnected
                    self.state.outgoingHeadline = "None / Cleared"
                    self.state.outgoingState = nil
                    self.updateIcon()
                } else {
                    // On wake or unlock, refresh foreground app
                    let (app, bundleId) = self.foregroundDetector.currentForegroundApp()
                    self.state.foregroundApp = app
                    self.state.detectedBundleId = bundleId
                    self.evaluatePresence(immediate: true)
                }
            }
        }

        discordClient.onStatusChanged = { [weak self] status, message in
            Task { @MainActor [weak self] in
                guard let self = self, self.started else { return }
                let eligible = self.settings.enableSharing && !self.settings.isPaused &&
                    !self.state.isSessionSuspended && self.settings.isValidDiscordClientId &&
                    self.state.foregroundApp != .none && self.computers.permission.allows(account: self.discordUserId)
                self.state.connectionStatus = eligible ? status : .disconnected
                if eligible, let message = message {
                    self.state.statusMessage = message
                    if status == .error {
                        self.state.lastError = message
                    }
                }
                self.updateIcon()
            }
        }

        discordClient.onActivityAcknowledged = { [weak self] activity in
            Task { @MainActor [weak self] in
                guard let self = self, self.started else { return }
                guard self.settings.enableSharing, !self.settings.isPaused,
                      !self.state.isSessionSuspended, self.settings.isValidDiscordClientId else { return }
                guard self.computers.permission.allows(account: self.discordUserId) else { return }
                self.state.lastUpdate = Date()
                self.updateIcon()
            }
        }
    }

    public func start() {
        guard !started else { return }
        started = true
        foregroundDetector.start()
        sessionObserver.start()
        computers.start(linked: settings.followComputers)
        if computers.hasSavedLink && !settings.followComputers {
            settings.followComputers = true
            settingsService.saveSettings(settings)
        }
        computerTimer = Timer.scheduledTimer(withTimeInterval: 1, repeats: true) { [weak self] _ in
            Task { @MainActor [weak self] in
                guard let self = self, self.started else { return }
                self.evaluatePresence()
            }
        }

        let (app, bundleId) = foregroundDetector.currentForegroundApp()
        self.state.foregroundApp = app
        self.state.detectedBundleId = bundleId

        evaluatePresence(immediate: false)
    }

    public func stop() {
        t3SetupTask?.cancel()
        stopUsagePolling(clear: true)
        t3Service.cancelSignIn()
        isFindingT3 = false
        started = false
        computerTimer?.invalidate(); computerTimer = nil
        computers.stop()
        foregroundDetector.stop()
        sessionObserver.stop()
        setAllEligibility(false)
        discordClient.clearAndCloseImmediately()
    }

    // MARK: - State Evaluation & Privacy Enforcement

    public func evaluatePresence(immediate: Bool = false) {
        guard started else { return }
        let now = Date()

        let localEligible = settings.enableSharing &&
                         !settings.isPaused &&
                         !state.isSessionSuspended &&
                         settings.isValidDiscordClientId

        computers.updateContext(linked: settings.followComputers, available: localEligible,
                                discordUserId: discordUserId)
        let isEligible = localEligible && computers.permission.allows(account: discordUserId)
        state.computerStatus = computers.status

        // Observe a running T3 desktop app even when minimized. Privacy and computer grants still gate polling.
        let t3Running = state.foregroundApp == .t3Code || foregroundDetector.isAppRunning(.t3Code)
        let isEligibleToPoll = isEligible && settings.showT3Activity && t3Running && settings.cardStyle != .appOnly
        setAllEligibility(isEligibleToPoll)

        // Combined snapshot over every environment; failing or stale environments contribute nothing.
        let combined = combinedSnapshot(now: now)
        let load = PresenceCardFormatter.workload(settings: settings, snapshot: combined, now: now)
        let hasUnfinishedWork = isEligibleToPoll && load.counts.values.contains { $0.totalWork > 0 }
        let featured = hasUnfinishedWork ? combinedSelector.evaluate(toolCounts: load.counts, now: now) : nil
        if !hasUnfinishedWork { combinedSelector.reset() }
        workClock.updateWork(hasUnfinishedWork: hasUnfinishedWork, sharingActive: isEligible && settings.showT3Activity, now: now)

        let otherRunning = foregroundDetector.isAppRunning(.claude) || foregroundDetector.isAppRunning(.codex)
        let selection = PresenceSelection.select(
            mode: settings.selectionMode,
            foreground: state.foregroundApp,
            t3Running: t3Running,
            otherSupportedAppRunning: otherRunning,
            hasFreshT3Work: hasUnfinishedWork)
        let displayedApp = selection.app
        workClock.updateShownApp(isEligible ? displayedApp : .none, now: now)

        updateUsagePolling(eligible: isEligibleToPoll && selection.richAllowed && displayedApp == .t3Code
                           && settings.cardStyle == .usage && anyUsageAllowed)
        let usage = settings.cardStyle == .usage ? usageSnapshot : nil

        let card = PresenceCardFormatter.format(
            settings: settings,
            snapshot: isEligibleToPoll ? combined : nil,
            usage: usage,
            timer: workClock.timerState,
            shownApp: displayedApp,
            now: now,
            featuredTool: featured,
            previousProjectKey: lastFeaturedProject,
            richAllowed: selection.richAllowed)
        if card.hasFreshWork { lastFeaturedProject = card.featuredProjectKey }
        state.t3ActiveWinner = card.featuredTool

        // The sharing gate stays authoritative: nothing is published unless every privacy condition holds.
        let gated: DiscordActivity? = (isEligible && displayedApp != .none) ? card.activity : nil
        let activity = DiscordArtwork.apply(to: gated, app: displayedApp, enabled: settings.showAppIcons)
        previewCard = isEligible ? card : PresenceCard(activity: nil)

        if isEligible {
            state.outgoingHeadline = ActivityFormatter.getHeadline(for: activity)
            state.outgoingState = activity?.state

            // Activity note per product language specification
            if !settings.showT3Activity || displayedApp != .t3Code || settings.cardStyle == .appOnly {
                state.t3ActivityNote = nil
            } else if !anyEnvironmentConnected {
                state.t3ActivityNote = "Connect T3 Code to show agent activity."
            } else if combined == nil {
                state.t3ActivityNote = "T3 activity isn’t available right now, so only the app name is shown."
            } else if !card.hasFreshWork {
                state.t3ActivityNote = selection.richAllowed
                    ? "No tasks right now, so only the app name is shown."
                    : nil
            } else {
                state.t3ActivityNote = nil
            }

            if state.foregroundApp != .none {
                state.statusMessage = "Preparing activity for \(state.foregroundApp.displayName)"
            } else {
                state.statusMessage = "Idle (Supported app not focused)"
            }
        } else {
            state.outgoingHeadline = "None / Cleared"
            state.outgoingState = nil
            state.t3ActivityNote = nil

            if !settings.enableSharing {
                state.statusMessage = "Disabled"
            } else if settings.isPaused {
                state.statusMessage = "Paused"
            } else if state.isSessionSuspended {
                state.statusMessage = "Session Locked / Inactive"
            } else if !settings.isValidDiscordClientId {
                state.statusMessage = settings.discordClientId.isEmpty ? "Missing Application ID" : "Invalid Application ID"
            }
        }

        if settings.followComputers && localEligible && (activity == nil || !isEligible) {
            discordClient.observeAccount(clientId: settings.discordClientId)
        } else {
            discordClient.updateActivity(activity, clientId: settings.discordClientId, immediate: immediate)
        }

        updateIcon()
    }

    private func setAllEligibility(_ eligible: Bool) {
        if t3Services.isEmpty {
            primaryT3Service.updateEligibility(isEligibleToPoll: eligible)
            return
        }
        for environment in settings.t3Environments {
            t3Services[environment.id]?.updateEligibility(isEligibleToPoll: eligible)
        }
    }

    private func combinedSnapshot(now: Date) -> T3CombinedSnapshot? {
        let environments = settings.t3Environments.compactMap { environment in
            t3Services[environment.id]?.environmentSnapshot(environmentId: environment.id, now: now)
        }
        return environments.isEmpty ? nil : T3CombinedSnapshot(environments: environments)
    }

    // MARK: - Usage polling (60 s while cardStyle = usage and the card is eligible)

    private func updateUsagePolling(eligible: Bool) {
        if eligible {
            guard usageTimer == nil else { return }
            usageTimer = Timer.scheduledTimer(withTimeInterval: T3UsageService.pollIntervalSeconds, repeats: true) { [weak self] _ in
                Task { @MainActor [weak self] in self?.pollUsageOnce() }
            }
            pollUsageOnce()
        } else if usageTimer != nil || usageSnapshot != nil || usagePollInFlight {
            stopUsagePolling(clear: true)
        }
    }

    private func stopUsagePolling(clear: Bool) {
        usageTimer?.invalidate()
        usageTimer = nil
        usageGeneration += 1
        usagePollTask?.cancel()
        usagePollTask = nil
        usagePollInFlight = false
        if clear { usageSnapshot = nil }
    }

    /// Restarts after a change that affects what is measured, so a stale value is never shown.
    private func restartUsagePolling() {
        let wasRunning = usageTimer != nil
        stopUsagePolling(clear: true)
        if wasRunning { evaluatePresence(immediate: false) }
    }

    private func pollUsageOnce() {
        guard !usagePollInFlight else { return }
        let now = Date()
        let combined = combinedSnapshot(now: now)
        let load = PresenceCardFormatter.workload(settings: settings, snapshot: combined, now: now)
        let allowed = settings.t3Environments.filter { usageStatus($0.id).isAllowed }
        let sessionStart = workClock.workSessionStart
        let requests = T3UsageService.plans(settings: settings, work: load.work, allowedEnvironments: allowed, sessionStart: sessionStart)
        guard !requests.isEmpty else { return }
        let pinnedKey = settings.pinnedTask.isEmpty ? nil : settings.pinnedTask
        let generation = usageGeneration
        let service = usageService
        usagePollInFlight = true
        usagePollTask = Task { [weak self] in
            let snapshot = await service.poll(requests: requests, pinnedTaskKey: pinnedKey, sessionStart: sessionStart, now: now)
            guard let self = self, !Task.isCancelled, self.usageGeneration == generation else { return }
            self.usagePollInFlight = false
            self.usagePollTask = nil
            self.usageSnapshot = snapshot
            self.refreshUsageStatuses()
            self.evaluatePresence(immediate: false)
        }
    }

    private func updateIcon() {
        if !settings.enableSharing {
            menuBarIconName = "antenna.radiowaves.left.and.right.slash"
        } else if settings.isPaused {
            menuBarIconName = "pause.circle"
        } else if state.isSessionSuspended {
            menuBarIconName = "lock.fill"
        } else if !settings.isValidDiscordClientId {
            menuBarIconName = "exclamationmark.triangle"
        } else {
            switch state.connectionStatus {
            case .connected:
                menuBarIconName = "circle.fill"
            case .connecting:
                menuBarIconName = "circle.dotted"
            case .error:
                menuBarIconName = "exclamationmark.circle"
            case .disconnected:
                menuBarIconName = "circle"
            }
        }
    }

    // MARK: - UI Actions

    public func linkComputer(address: String, code: String, name: String) {
        guard !computers.isBusy else { return }
        settings.followComputers = true
        settings.computerLinkAddress = address.trimmingCharacters(in: .whitespacesAndNewlines)
        settingsService.saveSettings(settings)
        computers.updateContext(linked: true, available: false, discordUserId: nil)
        discordClient.clearAndCloseImmediately()
        Task { _ = await computers.link(address: address, code: code, name: name); evaluatePresence() }
    }

    public func unlinkComputer() {
        guard !computers.isBusy else { return }
        settings.enableSharing = false
        settingsService.saveSettings(settings)
        computers.updateContext(linked: settings.followComputers, available: false, discordUserId: nil)
        discordClient.clearAndCloseImmediately()
        Task {
            if await computers.unlink() {
                settings.followComputers = false
                settingsService.saveSettings(settings)
            }
            evaluatePresence()
        }
    }

    public func toggleSharing() {
        settings.enableSharing.toggle()
        settingsService.saveSettings(settings)
        if !settings.enableSharing {
            discordClient.clearAndCloseImmediately()
            setAllEligibility(false)
            stopUsagePolling(clear: true)
        }
        evaluatePresence(immediate: true)
    }

    public func togglePause() {
        settings.isPaused.toggle()
        settingsService.saveSettings(settings)
        if settings.isPaused {
            discordClient.clearAndCloseImmediately()
            setAllEligibility(false)
            stopUsagePolling(clear: true)
        }
        evaluatePresence(immediate: true)
    }

    public func toggleT3Activity() {
        settings.showT3Activity.toggle()
        settingsService.saveSettings(settings)
        evaluatePresence(immediate: true)
    }

    public func toggleShowAppIcons() {
        settings.showAppIcons.toggle()
        settingsService.saveSettings(settings)
        evaluatePresence(immediate: true)
    }

    /// Legacy single-address override: edits the first environment in place (keeping its ID),
    /// creates it when the list is empty, or removes it when cleared (without deleting its sign-in).
    public func setT3Address(_ address: String) {
        t3SetupTask?.cancel()
        isFindingT3 = false
        t3SetupMessage = nil
        let trimmed = address.trimmingCharacters(in: .whitespacesAndNewlines)
        if trimmed.isEmpty {
            if let first = settings.t3Environments.first {
                if let service = t3Services.removeValue(forKey: first.id) {
                    service.handleEndpointChange(address: "")
                    service.updateEligibility(isEligibleToPoll: false)
                }
                environmentStatuses.removeValue(forKey: first.id)
                environmentDetails.removeValue(forKey: first.id)
                settings.t3Environments.removeFirst()
            }
        } else if let first = settings.t3Environments.first {
            let normalized = (try? T3Environment.make(fromAddress: trimmed))?.address ?? trimmed
            settings.t3Environments[0].address = normalized
            activityService(for: first).handleEndpointChange(address: trimmed)
        } else {
            let environment = T3Environment.migrated(fromLegacyAddress: trimmed)
            settings.t3Environments = [environment]
            activityService(for: environment).handleEndpointChange(address: trimmed)
        }
        settings.t3Address = trimmed
        settingsService.saveSettings(settings)
        refreshAggregateT3Status(message: nil)
        refreshUsageStatuses()
        evaluatePresence(immediate: false)
    }

    /// **Allow T3 activity**: signs in to the first environment that isn't connected, or discovers
    /// local T3 and adds it when no environment exists yet.
    public func connectT3() {
        guard !isFindingT3, state.t3ConnectionStatus != .openingSignIn else { return }
        let target = settings.t3Environments.first { environmentStatus($0.id) != .connected } ?? settings.t3Environments.first
        if let target = target {
            connectEnvironment(id: target.id)
            return
        }
        t3SetupTask?.cancel()
        t3SetupMessage = nil
        isFindingT3 = true
        t3SetupTask = Task {
            do {
                let address = try await t3Discovery.find()
                try Task.checkCancellation()
                isFindingT3 = false
                guard addEnvironment(address: address, label: "This Mac"),
                      let added = settings.t3Environments.last else { return }
                connectEnvironment(id: added.id)
            } catch {
                guard !Task.isCancelled else { return }
                isFindingT3 = false
                t3SetupMessage = error.localizedDescription
                evaluatePresence(immediate: false)
            }
        }
    }

    public func cancelT3SignIn() {
        t3SetupTask?.cancel()
        isFindingT3 = false
        t3SetupMessage = "Connection cancelled. You can try again whenever you’re ready."
        let opening = settings.t3Environments.compactMap { environment -> T3ActivityService? in
            guard let service = t3Services[environment.id], service.connectionStatus == .openingSignIn else { return nil }
            return service
        }
        if opening.isEmpty {
            t3Service.cancelSignIn()
        } else {
            opening.forEach { $0.cancelSignIn() }
        }
        evaluatePresence(immediate: false)
    }

    public func disconnectT3() {
        t3SetupTask?.cancel()
        isFindingT3 = false
        t3SetupMessage = nil
        t3Service.disconnect()
        evaluatePresence(immediate: true)
    }

    public func setDiscordClientId(_ clientId: String) {
        settings.discordClientId = clientId.trimmingCharacters(in: .whitespacesAndNewlines)
        settingsService.saveSettings(settings)
        evaluatePresence(immediate: true)
    }

    public func updateBundleMapping(bundleId: String, app: SupportedApp) {
        let key = bundleId.lowercased().trimmingCharacters(in: .whitespacesAndNewlines)
        guard !key.isEmpty else { return }
        settings.bundleMappings[key] = app
        settingsService.saveSettings(settings)
        foregroundDetector.updateBundleMappings(settings.bundleMappings)
        let (app, bundleId) = foregroundDetector.currentForegroundApp()
        state.foregroundApp = app
        state.detectedBundleId = bundleId
        evaluatePresence(immediate: false)
    }

    public func removeBundleMapping(bundleId: String) {
        let key = bundleId.lowercased().trimmingCharacters(in: .whitespacesAndNewlines)
        settings.bundleMappings.removeValue(forKey: key)
        settingsService.saveSettings(settings)
        foregroundDetector.updateBundleMappings(settings.bundleMappings)
        let (app, bundleId) = foregroundDetector.currentForegroundApp()
        state.foregroundApp = app
        state.detectedBundleId = bundleId
        evaluatePresence(immediate: false)
    }

    public func resetBundleMappingsToDefaults() {
        settings.bundleMappings = PresenceSettings.defaultBundleMappings
        settingsService.saveSettings(settings)
        foregroundDetector.updateBundleMappings(settings.bundleMappings)
        let (app, bundleId) = foregroundDetector.currentForegroundApp()
        state.foregroundApp = app
        state.detectedBundleId = bundleId
        evaluatePresence(immediate: false)
    }

    public func quitApplication() {
        stop()
        #if canImport(AppKit)
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.1) {
            NSApplication.shared.terminate(nil)
        }
        #endif
    }
}

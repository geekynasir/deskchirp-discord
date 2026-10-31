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
    public let t3Service: T3ActivityService

    private var started = false

    public init(
        settingsService: SettingsServicing = SettingsService.shared,
        foregroundDetector: ForegroundDetecting = WorkspaceForegroundDetector.shared,
        sessionObserver: SessionStateObserving = WorkspaceSessionObserver.shared,
        discordClient: DiscordIpcClient = DiscordIpcClient.shared,
        t3Service: T3ActivityService = T3ActivityService()
    ) {
        self.settingsService = settingsService
        self.foregroundDetector = foregroundDetector
        self.sessionObserver = sessionObserver
        self.discordClient = discordClient
        self.t3Service = t3Service

        let initialSettings = settingsService.loadSettings()
        self.settings = initialSettings
        self.state = PresenceState()

        setupBindings()

        // Restore saved T3 connection if address was previously configured
        if !initialSettings.t3Address.isEmpty {
            t3Service.restoreSavedConnection(address: initialSettings.t3Address)
        }
    }

    private func setupBindings() {
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
                    // Privacy rule: immediate clear on sleep or lock
                    self.t3Service.updateEligibility(isEligibleToPoll: false)
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

        t3Service.onStatusChanged = { [weak self] status, message in
            Task { @MainActor [weak self] in
                guard let self = self else { return }
                self.state.t3ConnectionStatus = status
                self.state.t3StatusDetail = message ?? status.recoveryText
                if status == .couldNotReach || status == .signInAgain {
                    self.state.t3LastError = message
                } else {
                    self.state.t3LastError = nil
                }
                self.evaluatePresence(immediate: false)
            }
        }

        t3Service.onActivityUpdated = { [weak self] snapshot, winner in
            Task { @MainActor [weak self] in
                guard let self = self, self.started else { return }
                self.state.t3ActiveWinner = winner
                self.evaluatePresence(immediate: false)
            }
        }

        discordClient.onStatusChanged = { [weak self] status, message in
            Task { @MainActor [weak self] in
                guard let self = self, self.started else { return }
                let eligible = self.settings.enableSharing && !self.settings.isPaused &&
                    !self.state.isSessionSuspended && self.settings.isValidDiscordClientId &&
                    self.state.foregroundApp != .none
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

        let (app, bundleId) = foregroundDetector.currentForegroundApp()
        self.state.foregroundApp = app
        self.state.detectedBundleId = bundleId

        evaluatePresence(immediate: false)
    }

    public func stop() {
        started = false
        foregroundDetector.stop()
        sessionObserver.stop()
        t3Service.updateEligibility(isEligibleToPoll: false)
        discordClient.clearAndCloseImmediately()
    }

    // MARK: - State Evaluation & Privacy Enforcement

    public func evaluatePresence(immediate: Bool = false) {
        guard started else { return }

        let isEligible = settings.enableSharing &&
                         !settings.isPaused &&
                         !state.isSessionSuspended &&
                         settings.isValidDiscordClientId

        // Update T3 polling eligibility: poll only when sharing is active, awake, T3 activity enabled, and T3 Code focused
        let isEligibleToPoll = isEligible &&
                              settings.showT3Activity &&
                              state.foregroundApp == .t3Code
        t3Service.updateEligibility(isEligibleToPoll: isEligibleToPoll)

        let t3Formatted: T3FormattedActivity?
        if state.foregroundApp == .t3Code {
            t3Formatted = settings.showT3Activity
                ? t3Service.currentFormattedActivity(showAppIcons: settings.showAppIcons)
                : T3ActivityFormatter.format(snapshot: nil, winningTool: nil, showAppIcons: settings.showAppIcons)
        } else {
            t3Formatted = nil
        }

        let activity = ActivityFormatter.formatActivity(
            app: state.foregroundApp,
            enableSharing: settings.enableSharing,
            isPaused: settings.isPaused,
            isSuspended: state.isSessionSuspended,
            isValidClientId: settings.isValidDiscordClientId,
            t3FormattedActivity: t3Formatted
        )

        if isEligible {
            state.outgoingHeadline = ActivityFormatter.getHeadline(for: activity)
            state.outgoingState = activity?.state

            // Activity note per product language specification
            if !settings.showT3Activity || state.foregroundApp != .t3Code {
                state.t3ActivityNote = nil
            } else if t3Service.connectionStatus != .connected {
                state.t3ActivityNote = "Connect T3 Code to show agent activity."
            } else if t3Service.currentSnapshot == nil {
                state.t3ActivityNote = "T3 activity isn’t available right now, so only the app name is shown."
            } else if let t3 = t3Formatted, t3.winningTool == nil {
                state.t3ActivityNote = "No tasks right now, so only the app name is shown."
            } else if t3Formatted == nil {
                state.t3ActivityNote = "T3 activity isn’t available right now, so only the app name is shown."
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

        discordClient.updateActivity(
            activity,
            clientId: settings.discordClientId,
            immediate: immediate
        )

        updateIcon()
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

    public func toggleSharing() {
        settings.enableSharing.toggle()
        settingsService.saveSettings(settings)
        if !settings.enableSharing {
            discordClient.clearAndCloseImmediately()
            t3Service.updateEligibility(isEligibleToPoll: false)
        }
        evaluatePresence(immediate: true)
    }

    public func togglePause() {
        settings.isPaused.toggle()
        settingsService.saveSettings(settings)
        if settings.isPaused {
            discordClient.clearAndCloseImmediately()
            t3Service.updateEligibility(isEligibleToPoll: false)
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

    public func setT3Address(_ address: String) {
        let trimmed = address.trimmingCharacters(in: .whitespacesAndNewlines)
        settings.t3Address = trimmed
        settingsService.saveSettings(settings)
        t3Service.handleEndpointChange(address: trimmed)
        evaluatePresence(immediate: false)
    }

    public func connectT3() {
        Task {
            do {
                try await t3Service.connect(address: settings.t3Address)
                self.evaluatePresence(immediate: true)
            } catch {
                self.evaluatePresence(immediate: false)
            }
        }
    }

    public func cancelT3SignIn() {
        t3Service.cancelSignIn()
        evaluatePresence(immediate: false)
    }

    public func disconnectT3() {
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

import Foundation
import Combine
#if canImport(AppKit)
import AppKit
#endif

/// Central coordinator managing application state, privacy enforcement, and Discord IPC.
@MainActor
public final class PresenceCoordinator: ObservableObject {
    @Published public private(set) var settings: PresenceSettings
    @Published public private(set) var state: PresenceState
    @Published public private(set) var menuBarIconName: String = "antenna.radiowaves.left.and.right.slash"

    private var started = false

    private let settingsService: SettingsServicing
    private let foregroundDetector: ForegroundDetecting
    private let sessionObserver: SessionStateObserving
    private let discordClient: DiscordIpcClient

    public init(
        settingsService: SettingsServicing = SettingsService.shared,
        foregroundDetector: ForegroundDetecting? = nil,
        sessionObserver: SessionStateObserving? = nil,
        discordClient: DiscordIpcClient? = nil
    ) {
        let loadedSettings = settingsService.loadSettings()
        self.settings = loadedSettings
        self.settingsService = settingsService

        let assignedDetector = foregroundDetector ?? WorkspaceForegroundDetector(bundleMappings: loadedSettings.bundleMappings)
        self.foregroundDetector = assignedDetector

        let assignedSessionObserver = sessionObserver ?? WorkspaceSessionObserver()
        self.sessionObserver = assignedSessionObserver

        let assignedClient = discordClient ?? DiscordIpcClient()
        self.discordClient = assignedClient

        self.state = PresenceState(
            connectionStatus: .disconnected,
            foregroundApp: .none,
            detectedBundleId: nil,
            outgoingHeadline: "None / Cleared",
            isSessionSuspended: assignedSessionObserver.isSuspended,
            lastUpdate: nil,
            statusMessage: loadedSettings.enableSharing ? "Ready" : "Disabled",
            lastError: nil
        )

        self.setupBindings()
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
                    self.discordClient.clearAndCloseImmediately()
                    self.state.connectionStatus = .disconnected
                    self.state.outgoingHeadline = "None / Cleared (Locked or Sleeping)"
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
        discordClient.clearAndCloseImmediately()
    }

    // MARK: - State Evaluation & Privacy Enforcement

    public func evaluatePresence(immediate: Bool = false) {
        guard started else { return }
        let isEligible = settings.enableSharing &&
                         !settings.isPaused &&
                         !state.isSessionSuspended &&
                         settings.isValidDiscordClientId

        let activity = ActivityFormatter.formatActivity(
            app: state.foregroundApp,
            enableSharing: settings.enableSharing,
            isPaused: settings.isPaused,
            isSuspended: state.isSessionSuspended,
            isValidClientId: settings.isValidDiscordClientId
        )

        if isEligible {
            state.outgoingHeadline = ActivityFormatter.getHeadline(for: activity)
            if state.foregroundApp != .none {
                state.statusMessage = "Preparing activity for \(state.foregroundApp.displayName)"
            } else {
                state.statusMessage = "Idle (Supported app not focused)"
            }
        } else {
            state.outgoingHeadline = "None / Cleared"
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
        }
        evaluatePresence(immediate: true)
    }

    public func togglePause() {
        settings.isPaused.toggle()
        settingsService.saveSettings(settings)
        if settings.isPaused {
            discordClient.clearAndCloseImmediately()
        }
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

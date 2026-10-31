import Foundation

public enum PresenceConnectionStatus: String, Codable, Equatable, Sendable {
    case disconnected = "Disconnected"
    case connecting = "Connecting"
    case connected = "Connected"
    case error = "Error"
}

public struct PresenceState: Equatable, Sendable {
    public var connectionStatus: PresenceConnectionStatus
    public var foregroundApp: SupportedApp
    public var detectedBundleId: String?
    public var outgoingHeadline: String
    public var outgoingState: String?
    public var isSessionSuspended: Bool
    public var lastUpdate: Date?
    public var statusMessage: String
    public var lastError: String?

    // T3-specific state
    public var t3ConnectionStatus: T3ConnectionStatus
    public var t3StatusDetail: String?
    public var t3ActivityNote: String?
    public var t3ActiveWinner: String?
    public var t3LastError: String?

    public init(
        connectionStatus: PresenceConnectionStatus = .disconnected,
        foregroundApp: SupportedApp = .none,
        detectedBundleId: String? = nil,
        outgoingHeadline: String = "None / Cleared",
        outgoingState: String? = nil,
        isSessionSuspended: Bool = false,
        lastUpdate: Date? = nil,
        statusMessage: String = "Disabled",
        lastError: String? = nil,
        t3ConnectionStatus: T3ConnectionStatus = .notConnected,
        t3StatusDetail: String? = nil,
        t3ActivityNote: String? = nil,
        t3ActiveWinner: String? = nil,
        t3LastError: String? = nil
    ) {
        self.connectionStatus = connectionStatus
        self.foregroundApp = foregroundApp
        self.detectedBundleId = detectedBundleId
        self.outgoingHeadline = outgoingHeadline
        self.outgoingState = outgoingState
        self.isSessionSuspended = isSessionSuspended
        self.lastUpdate = lastUpdate
        self.statusMessage = statusMessage
        self.lastError = lastError
        self.t3ConnectionStatus = t3ConnectionStatus
        self.t3StatusDetail = t3StatusDetail ?? t3ConnectionStatus.recoveryText
        self.t3ActivityNote = t3ActivityNote
        self.t3ActiveWinner = t3ActiveWinner
        self.t3LastError = t3LastError
    }
}

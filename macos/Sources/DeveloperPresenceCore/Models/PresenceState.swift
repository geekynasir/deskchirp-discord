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
    public var isSessionSuspended: Bool
    public var lastUpdate: Date?
    public var statusMessage: String
    public var lastError: String?

    public init(
        connectionStatus: PresenceConnectionStatus = .disconnected,
        foregroundApp: SupportedApp = .none,
        detectedBundleId: String? = nil,
        outgoingHeadline: String = "None / Cleared",
        isSessionSuspended: Bool = false,
        lastUpdate: Date? = nil,
        statusMessage: String = "Disabled",
        lastError: String? = nil
    ) {
        self.connectionStatus = connectionStatus
        self.foregroundApp = foregroundApp
        self.detectedBundleId = detectedBundleId
        self.outgoingHeadline = outgoingHeadline
        self.isSessionSuspended = isSessionSuspended
        self.lastUpdate = lastUpdate
        self.statusMessage = statusMessage
        self.lastError = lastError
    }
}

import Foundation

/// Safe Discord Rich Presence Activity payload.
/// Under version 1 spec, contains ONLY static details.
/// No timestamps, assets, party, secrets, buttons, project, or model fields.
public struct DiscordActivity: Codable, Equatable, Sendable {
    public let details: String

    public init(details: String) {
        self.details = details
    }
}

/// Opcode 1 SET_ACTIVITY request payload sent over Discord IPC.
public struct DiscordSetActivityPayload: Codable, Sendable {
    public let cmd: String
    public let args: DiscordSetActivityArgs
    public let nonce: String

    public init(pid: Int32, activity: DiscordActivity?, nonce: String = UUID().uuidString) {
        self.cmd = "SET_ACTIVITY"
        self.args = DiscordSetActivityArgs(pid: pid, activity: activity)
        self.nonce = nonce
    }
}

public struct DiscordSetActivityArgs: Codable, Sendable {
    public let pid: Int32
    public let activity: DiscordActivity?

    public init(pid: Int32, activity: DiscordActivity?) {
        self.pid = pid
        self.activity = activity
    }

    private enum CodingKeys: String, CodingKey {
        case pid
        case activity
    }

    public init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        self.pid = try container.decode(Int32.self, forKey: .pid)
        self.activity = try container.decodeIfPresent(DiscordActivity.self, forKey: .activity)
    }

    public func encode(to encoder: Encoder) throws {
        var container = encoder.container(keyedBy: CodingKeys.self)
        try container.encode(pid, forKey: .pid)
        if let activity = activity {
            try container.encode(activity, forKey: .activity)
        } else {
            // Explicitly encode null to clear activity per Discord IPC protocol
            try container.encodeNil(forKey: .activity)
        }
    }
}

/// Opcode 0 Handshake payload.
public struct DiscordHandshakePayload: Codable, Sendable {
    public let v: Int
    public let client_id: String

    public init(clientId: String) {
        self.v = 1
        self.client_id = clientId
    }
}

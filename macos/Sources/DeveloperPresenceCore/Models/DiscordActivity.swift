import Foundation

/// Safe Discord Rich Presence Activity assets payload.
/// Only public asset keys (`t3_code`, `harness_claude`, `harness_codex`, `harness_antigravity`)
/// are used when the user explicitly enables app icons.
public struct DiscordActivityAssets: Codable, Equatable, Sendable {
    public let large_image: String?
    public let large_text: String?
    public let small_image: String?
    public let small_text: String?

    public init(
        largeImage: String? = nil,
        largeText: String? = nil,
        smallImage: String? = nil,
        smallText: String? = nil
    ) {
        self.large_image = largeImage
        self.large_text = largeText
        self.small_image = smallImage
        self.small_text = smallText
    }

    private enum CodingKeys: String, CodingKey {
        case large_image
        case large_text
        case small_image
        case small_text
    }

    public init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        self.large_image = try container.decodeIfPresent(String.self, forKey: .large_image)
        self.large_text = try container.decodeIfPresent(String.self, forKey: .large_text)
        self.small_image = try container.decodeIfPresent(String.self, forKey: .small_image)
        self.small_text = try container.decodeIfPresent(String.self, forKey: .small_text)
    }

    public func encode(to encoder: Encoder) throws {
        var container = encoder.container(keyedBy: CodingKeys.self)
        if let large_image = large_image {
            try container.encode(large_image, forKey: .large_image)
        }
        if let large_text = large_text {
            try container.encode(large_text, forKey: .large_text)
        }
        if let small_image = small_image {
            try container.encode(small_image, forKey: .small_image)
        }
        if let small_text = small_text {
            try container.encode(small_text, forKey: .small_text)
        }
    }
}

/// Safe Discord Rich Presence Activity payload.
/// Under version 2 specification, contains safe details (app name and optional generic agent tool),
/// optional state line (task/helper counts), and optional public app icons.
/// No elapsed timestamps, secrets, buttons, projects, models, prompts, titles, or private URLs.
public struct DiscordActivity: Codable, Equatable, Sendable {
    public let details: String
    public let state: String?
    public let assets: DiscordActivityAssets?

    public init(
        details: String,
        state: String? = nil,
        assets: DiscordActivityAssets? = nil
    ) {
        self.details = details
        self.state = state
        self.assets = assets
    }

    private enum CodingKeys: String, CodingKey {
        case details
        case state
        case assets
    }

    public init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        self.details = try container.decode(String.self, forKey: .details)
        self.state = try container.decodeIfPresent(String.self, forKey: .state)
        self.assets = try container.decodeIfPresent(DiscordActivityAssets.self, forKey: .assets)
    }

    public func encode(to encoder: Encoder) throws {
        var container = encoder.container(keyedBy: CodingKeys.self)
        try container.encode(details, forKey: .details)
        if let state = state {
            try container.encode(state, forKey: .state)
        }
        if let assets = assets {
            try container.encode(assets, forKey: .assets)
        }
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

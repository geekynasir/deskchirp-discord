import Foundation

/// Supported developer applications for presence detection.
public enum SupportedApp: String, Codable, CaseIterable, Equatable, Sendable {
    case none = "none"
    case t3Code = "t3"
    case claude = "claude"
    case codex = "codex"

    /// User-visible application name.
    public var displayName: String {
        switch self {
        case .none:
            return "None / Other"
        case .t3Code:
            return "T3 Code"
        case .claude:
            return "Claude Desktop"
        case .codex:
            return "Codex Desktop"
        }
    }

    /// Static presence details string per specification.
    /// strictly whitelisted details; no extra tokens, timestamps, or telemetry.
    public var presenceDetails: String? {
        switch self {
        case .none:
            return nil
        case .t3Code:
            return "Using T3 Code"
        case .claude:
            return "Using Claude"
        case .codex:
            return "Using Codex"
        }
    }
}

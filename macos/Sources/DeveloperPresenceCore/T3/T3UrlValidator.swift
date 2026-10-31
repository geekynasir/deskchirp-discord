import Foundation

public struct T3NormalizedEndpoint: Equatable, Sendable {
    public let origin: String
    public let mcpUrl: URL
    public let authorizationServerMetadataUrl: URL
    public let protectedResourceMetadataUrl: URL
    public let protectedResourceMetadataFallbackUrl: URL

    public init(origin: String) {
        self.origin = origin
        self.mcpUrl = URL(string: "\(origin)/mcp")!
        self.authorizationServerMetadataUrl = URL(string: "\(origin)/.well-known/oauth-authorization-server")!
        self.protectedResourceMetadataUrl = URL(string: "\(origin)/.well-known/oauth-protected-resource/mcp")!
        self.protectedResourceMetadataFallbackUrl = URL(string: "\(origin)/.well-known/oauth-protected-resource")!
    }
}

public enum T3UrlValidationError: LocalizedError, Equatable {
    case empty
    case plainHttpNonLocalhost
    case invalidFormat

    public var errorDescription: String? {
        switch self {
        case .empty:
            return "Paste the address from T3 Code first."
        case .plainHttpNonLocalhost:
            return "Use an address that starts with https://. Plain http:// works only for T3 Code on this computer."
        case .invalidFormat:
            return "This doesn’t look like a T3 Code address. Copy it again from Settings → Environments → Copy MCP URL."
        }
    }
}

/// Strict validator and normalizer for user-entered T3 Code addresses per specification:
/// - HTTPS or HTTP loopback only (exact localhost, 127.0.0.1, [::1]).
/// - Rejects URL userinfo, queries, fragments, and unrecognized paths.
/// - Normalizes origin or /mcp URL to its /mcp endpoint.
public enum T3UrlValidator {
    private static let loopbackHosts: Set<String> = [
        "localhost",
        "127.0.0.1",
        "[::1]",
        "::1"
    ]

    public static func validateAndNormalize(_ input: String) throws -> T3NormalizedEndpoint {
        let trimmed = input.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else {
            throw T3UrlValidationError.empty
        }

        // Reject userinfo, queries, or fragments embedded in the raw string
        if trimmed.contains("@") || trimmed.contains("?") || trimmed.contains("#") {
            throw T3UrlValidationError.invalidFormat
        }

        guard let components = URLComponents(string: trimmed),
              let scheme = components.scheme?.lowercased(),
              let host = components.host?.lowercased(),
              !host.isEmpty else {
            throw T3UrlValidationError.invalidFormat
        }

        if components.user != nil || components.password != nil || components.query != nil || components.fragment != nil {
            throw T3UrlValidationError.invalidFormat
        }

        let isLoopback = loopbackHosts.contains(host)

        if scheme == "http" {
            guard isLoopback else {
                throw T3UrlValidationError.plainHttpNonLocalhost
            }
        } else if scheme == "https" {
            // HTTPS allowed for any valid host
        } else {
            throw T3UrlValidationError.invalidFormat
        }

        let path = components.path.trimmingCharacters(in: .whitespacesAndNewlines)
        let normalizedPath = path.hasSuffix("/") && path.count > 1 ? String(path.dropLast()) : path
        if !normalizedPath.isEmpty && normalizedPath != "/" && normalizedPath != "/mcp" {
            throw T3UrlValidationError.invalidFormat
        }

        let formattedHost: String
        if host == "::1" {
            formattedHost = "[::1]"
        } else {
            formattedHost = host
        }

        let origin: String
        if let port = components.port {
            let isDefaultPort = (scheme == "http" && port == 80) || (scheme == "https" && port == 443)
            if isDefaultPort {
                origin = "\(scheme)://\(formattedHost)"
            } else {
                origin = "\(scheme)://\(formattedHost):\(port)"
            }
        } else {
            origin = "\(scheme)://\(formattedHost)"
        }

        return T3NormalizedEndpoint(origin: origin)
    }
}

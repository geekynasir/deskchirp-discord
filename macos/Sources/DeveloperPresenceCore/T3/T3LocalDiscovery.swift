import Foundation

public enum T3DiscoveryError: LocalizedError {
    case notFound, ambiguous
    public var errorDescription: String? {
        switch self {
        case .notFound: return "Open T3 Code on this computer, then try again. If your tasks run on another computer, add that environment under T3 environments."
        case .ambiguous: return "More than one T3 Code environment was found. Add the one you want under T3 environments."
        }
    }
}

/// Reads only public runtime address metadata, never T3 credentials, catalogs or conversations.
public struct T3LocalDiscovery: Sendable {
    private let runtimePath: String
    private let transport: HTTPTransporting

    public init(runtimePath: String? = nil, transport: HTTPTransporting? = nil) {
        let home = ProcessInfo.processInfo.environment["T3CODE_HOME"] ?? FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent(".t3").path
        self.runtimePath = runtimePath ?? home + "/userdata/server-runtime.json"
        self.transport = transport ?? URLSessionHTTPTransport(maxMetadataBytes: 16_384, requestTimeout: 2, resourceTimeout: 2)
    }

    public func find() async throws -> String {
        var candidates = Set(["http://127.0.0.1:3773/mcp"])
        if let file = FileHandle(forReadingAtPath: runtimePath) {
            defer { try? file.close() }
            if let bytes = try? file.read(upToCount: 16_385), bytes.count <= 16_384,
               let candidate = Self.parseRuntime(bytes) { candidates.insert(candidate) }
        }
        var found: [String] = []
        for candidate in candidates.sorted() {
            try Task.checkCancellation()
            let endpoint = try T3UrlValidator.validateAndNormalize(candidate)
            var request = URLRequest(url: endpoint.protectedResourceMetadataUrl)
            request.timeoutInterval = 2
            request.setValue("application/json", forHTTPHeaderField: "Accept")
            do {
                let (bytes, response) = try await transport.send(request: request)
                if response.statusCode == 200, bytes.count <= 16_384,
                   Self.matchesMetadata(bytes, endpoint: endpoint) { found.append(candidate) }
            } catch { try Task.checkCancellation() }
        }
        try Task.checkCancellation()
        guard found.count == 1 else { throw found.isEmpty ? T3DiscoveryError.notFound : T3DiscoveryError.ambiguous }
        return found[0]
    }

    public static func parseRuntime(_ bytes: Data) -> String? {
        struct Runtime: Decodable { let version: Int; let pid: Int; let port: Int; let host: String? }
        guard let info = try? JSONDecoder().decode(Runtime.self, from: bytes),
              info.version == 1, info.pid > 0, (1...65535).contains(info.port) else { return nil }
        let host = info.host == "::1" ? "[::1]" : "127.0.0.1"
        return "http://\(host):\(info.port)/mcp"
    }

    public static func matchesMetadata(_ bytes: Data, endpoint: T3NormalizedEndpoint) -> Bool {
        struct Metadata: Decodable { let resource: String; let authorization_servers: [String]; let scopes_supported: [String] }
        guard let info = try? JSONDecoder().decode(Metadata.self, from: bytes) else { return false }
        return info.resource == endpoint.mcpUrl.absoluteString && info.authorization_servers.contains(endpoint.origin) && info.scopes_supported.contains("orchestration:read")
    }
}

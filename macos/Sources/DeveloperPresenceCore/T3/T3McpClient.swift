import Foundation
import CoreFoundation

public enum McpClientError: LocalizedError, Equatable {
    case unauthorized
    case responseTooLarge
    case snapshotCapped
    case mismatchedId
    case httpError(statusCode: Int)
    case jsonRpcError(code: Int, message: String)
    case invalidResponse(String)

    public var errorDescription: String? {
        switch self {
        case .unauthorized:
            return "Your T3 Code sign-in expired. Connect again to keep showing activity."
        case .responseTooLarge:
            return "T3 Code returned a response exceeding the maximum size limit."
        case .snapshotCapped:
            return "T3 Code thread snapshot exceeded maximum allowable rows/pages."
        case .mismatchedId:
            return "JSON-RPC response ID does not match request ID."
        case .httpError(let code):
            return "MCP HTTP request failed with status \(code)."
        case .jsonRpcError(let code, let message):
            return "MCP JSON-RPC error (\(code)): \(message)"
        case .invalidResponse(let msg):
            return "Invalid MCP response: \(msg)"
        }
    }
}

/// MCP Streamable HTTP client per specification:
/// - Negotiates protocol version 2025-06-18.
/// - Calls `initialize`, sends `notifications/initialized`.
/// - Strictly allowlists only `orchestrator_capabilities`, `t3_thread_list`, and `t3_project_list`.
/// - Supports both application/json and text/event-stream (SSE).
/// - Preserves MCP session header (`mcp-session-id`) and protocol header (`mcp-protocol-version`).
/// - Enforces response size bounds, timeouts, pagination caps, and deduplication.
/// - Finishes matching JSON-RPC responses immediately without waiting for stream EOF.
public final class T3McpClient: @unchecked Sendable {
    /// The only MCP tools Deskchirp ever calls. All need only `orchestration:read`.
    public static let allowedTools: Set<String> = ["orchestrator_capabilities", "t3_thread_list", "t3_project_list"]
    /// Project titles change rarely; refresh them at most this often.
    public static let projectRefreshSeconds: TimeInterval = 300

    private let transport: HTTPTransporting
    private let maxResponseBytes: Int
    private let maxPages: Int
    private let maxTotalRows: Int
    private let lock = NSLock()

    private var isInitialized: Bool = false
    private var sessionId: String?
    private var protocolVersion: String? = "2025-06-18"
    private var currentEndpointOrigin: String?
    private var currentToken: String?
    private var cachedProjects: [T3ProjectInfo] = []
    private var projectsFetchedAt: Date?

    public init(
        transport: HTTPTransporting = URLSessionHTTPTransport.shared,
        maxResponseBytes: Int = 1_048_576, // 1 MB cap
        maxPages: Int = 10,
        maxTotalRows: Int = 1000
    ) {
        self.transport = transport
        self.maxResponseBytes = maxResponseBytes
        self.maxPages = maxPages
        self.maxTotalRows = maxTotalRows
    }

    public func resetSession() {
        lock.lock()
        isInitialized = false
        sessionId = nil
        protocolVersion = "2025-06-18"
        currentEndpointOrigin = nil
        currentToken = nil
        cachedProjects = []
        projectsFetchedAt = nil
        lock.unlock()
    }

    /// Polls capabilities, the thread list, and (best effort, cached) the project list.
    /// A project-list failure other than an expired sign-in never fails the thread snapshot.
    public func pollEnvironment(
        endpoint: T3NormalizedEndpoint,
        token: String,
        now: Date = Date()
    ) async throws -> (providers: [T3ProviderInfo], threads: [T3ThreadItem], projects: [T3ProjectInfo]) {
        let (providers, threads) = try await poll(endpoint: endpoint, token: token)
        lock.lock()
        let fetchedAt = projectsFetchedAt
        let cached = cachedProjects
        lock.unlock()
        if let fetchedAt = fetchedAt, now.timeIntervalSince(fetchedAt) < Self.projectRefreshSeconds, now >= fetchedAt {
            return (providers, threads, cached)
        }
        do {
            let projects = try await fetchProjectList(endpoint: endpoint, token: token)
            lock.lock()
            cachedProjects = projects
            projectsFetchedAt = now
            lock.unlock()
            return (providers, threads, projects)
        } catch McpClientError.unauthorized {
            throw McpClientError.unauthorized
        } catch {
            // Older T3 builds may not offer the tool; retry on the normal refresh cadence.
            lock.lock()
            projectsFetchedAt = now
            lock.unlock()
            return (providers, threads, cached)
        }
    }

    private func fetchProjectList(endpoint: T3NormalizedEndpoint, token: String) async throws -> [T3ProjectInfo] {
        var projects: [T3ProjectInfo] = []
        var seen = Set<String>()
        var cursor: Any?
        var requestId = 1000
        for _ in 0..<maxPages {
            var arguments: [String: Any] = [:]
            if let cursor = cursor { arguments["cursor"] = cursor }
            let result = try await postJsonRpc(
                endpoint: endpoint,
                token: token,
                method: "tools/call",
                params: ["name": "t3_project_list", "arguments": arguments],
                id: requestId
            )
            requestId += 1
            let resultDict: [String: Any]
            if let structured = result["structuredContent"] as? [String: Any] {
                resultDict = structured
            } else if result["projects"] != nil {
                resultDict = result
            } else if let content = result["content"] as? [[String: Any]],
                      let firstText = content.first?["text"] as? String,
                      let textData = firstText.data(using: .utf8),
                      let parsed = try? JSONSerialization.jsonObject(with: textData) as? [String: Any] {
                resultDict = parsed
            } else {
                throw McpClientError.invalidResponse("Project list result is missing project data.")
            }
            guard let rawProjects = resultDict["projects"] as? [[String: Any]] else {
                throw McpClientError.invalidResponse("projects field missing in project list.")
            }
            for raw in rawProjects {
                let id = (raw["projectId"] as? String) ?? (raw["id"] as? String) ?? ""
                guard !id.isEmpty, !seen.contains(id) else { continue }
                seen.insert(id)
                let title = (raw["title"] as? String) ?? (raw["name"] as? String)
                projects.append(T3ProjectInfo(projectId: id, title: title))
                if projects.count > maxTotalRows { throw McpClientError.snapshotCapped }
            }
            if let next = resultDict["nextCursor"], !(next is NSNull), !rawProjects.isEmpty {
                cursor = next
            } else {
                return projects
            }
        }
        throw McpClientError.snapshotCapped
    }

    public func poll(
        endpoint: T3NormalizedEndpoint,
        token: String
    ) async throws -> (providers: [T3ProviderInfo], threads: [T3ThreadItem]) {
        lock.lock()
        let originMatches = (self.currentEndpointOrigin == endpoint.origin)
        let tokenMatches = (self.currentToken == token)
        let needsInit = !self.isInitialized || !originMatches || !tokenMatches

        if !originMatches || !tokenMatches {
            self.isInitialized = false
            self.sessionId = nil
            self.protocolVersion = "2025-06-18"
            self.currentEndpointOrigin = endpoint.origin
            self.currentToken = token
        }
        lock.unlock()

        do {
        if needsInit {
            // 1. Initialize MCP connection once per endpoint/token identity
            try await initialize(endpoint: endpoint, token: token)

            // 2. Initialized notification
            try await sendInitializedNotification(endpoint: endpoint, token: token)

            lock.lock()
            self.isInitialized = true
            lock.unlock()
        }

            // 3. Tool call 1: orchestrator_capabilities
            let providers = try await fetchCapabilities(endpoint: endpoint, token: token)

            // 4. Tool call 2: t3_thread_list (with pagination and capping)
            let threads = try await fetchThreadList(endpoint: endpoint, token: token)

            return (providers, threads)
        } catch {
            resetSession()
            throw error
        }
    }

    private func initialize(endpoint: T3NormalizedEndpoint, token: String) async throws {
        let initParams: [String: Any] = [
            "protocolVersion": "2025-06-18",
            "capabilities": [String: Any](),
            "clientInfo": [
                "name": "Deskchirp",
                "version": "0.1.0-preview.1"
            ]
        ]

        let result = try await postJsonRpc(
            endpoint: endpoint,
            token: token,
            method: "initialize",
            params: initParams,
            id: 1
        )

        guard let version = result["protocolVersion"] as? String,
              ["2025-03-26", "2025-06-18"].contains(version) else {
            throw McpClientError.invalidResponse("Unsupported protocol version.")
        }
        lock.lock()
        self.protocolVersion = version
        lock.unlock()
    }

    private func sendInitializedNotification(endpoint: T3NormalizedEndpoint, token: String) async throws {
        _ = try await postJsonRpc(
            endpoint: endpoint,
            token: token,
            method: "notifications/initialized",
            params: [:],
            id: nil
        )
    }

    private func fetchCapabilities(endpoint: T3NormalizedEndpoint, token: String) async throws -> [T3ProviderInfo] {
        let toolCallParams: [String: Any] = [
            "name": "orchestrator_capabilities",
            "arguments": [String: Any]()
        ]

        let result = try await postJsonRpc(
            endpoint: endpoint,
            token: token,
            method: "tools/call",
            params: toolCallParams,
            id: 2
        )

        let providersData: [[String: Any]]
        if let structured = result["structuredContent"] as? [String: Any],
           let list = structured["providers"] as? [[String: Any]] {
            providersData = list
        } else if let directList = result["providers"] as? [[String: Any]] {
            providersData = directList
        } else if let content = result["content"] as? [[String: Any]],
                  let firstText = content.first?["text"] as? String,
                  let textData = firstText.data(using: .utf8),
                  let parsed = try? JSONSerialization.jsonObject(with: textData) as? [String: Any],
                  let list = parsed["providers"] as? [[String: Any]] {
            providersData = list
        } else {
            throw McpClientError.invalidResponse("Missing providers.")
        }

        return try providersData.map { dict -> T3ProviderInfo in
            guard let id = dict["providerInstanceId"] as? String,
                  !id.isEmpty, let driver = dict["driverKind"] as? String, !driver.isEmpty else {
                throw McpClientError.invalidResponse("Malformed provider.")
            }
            return T3ProviderInfo(providerInstanceId: id, driverKind: driver)
        }
    }

    private func fetchThreadList(endpoint: T3NormalizedEndpoint, token: String) async throws -> [T3ThreadItem] {
        var collectedThreads: [T3ThreadItem] = []
        var nextCursor: Int? = nil
        var pageCount = 0
        var currentRequestId = 3

        while true {
            pageCount += 1
            if pageCount > maxPages {
                throw McpClientError.snapshotCapped
            }

            var arguments: [String: Any] = [
                "allProjects": true,
                "includeSubagents": true,
                "limit": 100
            ]
            if let cursor = nextCursor {
                arguments["cursor"] = cursor
            }

            let toolCallParams: [String: Any] = [
                "name": "t3_thread_list",
                "arguments": arguments
            ]

            let result = try await postJsonRpc(
                endpoint: endpoint,
                token: token,
                method: "tools/call",
                params: toolCallParams,
                id: currentRequestId
            )
            currentRequestId += 1

            let resultDict: [String: Any]
            if let structured = result["structuredContent"] as? [String: Any] {
                resultDict = structured
            } else if let directThreads = result["threads"] as? [[String: Any]] {
                resultDict = result
            } else if let content = result["content"] as? [[String: Any]],
                      let firstText = content.first?["text"] as? String,
                      let textData = firstText.data(using: .utf8),
                      let parsed = try? JSONSerialization.jsonObject(with: textData) as? [String: Any] {
                resultDict = parsed
            } else {
                throw McpClientError.invalidResponse("Thread list result is missing thread data.")
            }

            guard let rawThreads = resultDict["threads"] as? [[String: Any]] else {
                throw McpClientError.invalidResponse("threads field missing in thread list.")
            }

            guard let total = resultDict["total"] as? Int, total >= 0,
                  CFGetTypeID(resultDict["total"] as! NSNumber) != CFBooleanGetTypeID(), resultDict.keys.contains("nextCursor") else {
                throw McpClientError.invalidResponse("Missing pagination metadata.")
            }
            if total > maxTotalRows { throw McpClientError.snapshotCapped }
            for threadObj in rawThreads {
                guard let threadId = threadObj["threadId"] as? String,
                      let providerInstanceId = threadObj["providerInstanceId"] as? String,
                      let status = threadObj["status"] as? String,
                      !threadId.isEmpty, !providerInstanceId.isEmpty, !status.isEmpty else {
                    throw McpClientError.invalidResponse("Malformed thread.")
                }
                let relationship = threadObj["relationshipToParent"] as? String
                if let raw = threadObj["relationshipToParent"], !(raw is NSNull),
                   relationship != "fork" && relationship != "subagent" {
                    throw McpClientError.invalidResponse("Malformed relationship.")
                }
                collectedThreads.append(T3ThreadItem(
                    threadId: threadId,
                    providerInstanceId: providerInstanceId,
                    relationshipToParent: relationship,
                    status: status,
                    projectId: Self.optionalString(threadObj["projectId"]),
                    model: Self.modelString(threadObj["model"]),
                    parentThreadId: Self.optionalString(threadObj["parentThreadId"]),
                    createdAt: Self.optionalString(threadObj["createdAt"]),
                    updatedAt: Self.optionalString(threadObj["updatedAt"]),
                    title: Self.optionalString(threadObj["title"])
                ))
            }

            if collectedThreads.count > maxTotalRows {
                throw McpClientError.snapshotCapped
            }

            if let rawCursor = resultDict["nextCursor"], !(rawCursor is NSNull) {
                guard let cursor = rawCursor as? Int, CFGetTypeID(rawCursor as! NSNumber) != CFBooleanGetTypeID(),
                      cursor > (nextCursor ?? 0), !rawThreads.isEmpty else {
                    throw McpClientError.snapshotCapped
                }
                nextCursor = cursor
            } else {
                guard collectedThreads.count >= total else {
                    throw McpClientError.invalidResponse("Incomplete thread snapshot.")
                }
                break
            }
        }

        // Deduplicate thread IDs within the connected environment per specification
        var seenIds = Set<String>()
        var deduplicated: [T3ThreadItem] = []
        for thread in collectedThreads {
            if !seenIds.contains(thread.threadId) {
                seenIds.insert(thread.threadId)
                deduplicated.append(thread)
            }
        }

        return deduplicated
    }

    private static func optionalString(_ value: Any?) -> String? {
        guard let string = value as? String, !string.isEmpty else { return nil }
        return string
    }

    /// `model` may be a plain string or an object holding a `model` string. Anything else is ignored.
    private static func modelString(_ value: Any?) -> String? {
        if let string = optionalString(value) { return string }
        if let object = value as? [String: Any] { return optionalString(object["model"]) }
        return nil
    }

    private func postJsonRpc(
        endpoint: T3NormalizedEndpoint,
        token: String,
        method: String,
        params: [String: Any],
        id: Int?
    ) async throws -> [String: Any] {
        if method == "tools/call" {
            guard let tool = params["name"] as? String, Self.allowedTools.contains(tool) else {
                throw McpClientError.invalidResponse("Tool is not allowlisted.")
            }
        }
        var req = URLRequest(url: endpoint.mcpUrl)
        req.httpMethod = "POST"
        req.setValue("application/json", forHTTPHeaderField: "Content-Type")
        req.setValue("application/json, text/event-stream", forHTTPHeaderField: "Accept")
        req.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")

        lock.lock()
        // NEVER attach mcp-session-id to initialize
        if method != "initialize", let sId = sessionId {
            req.setValue(sId, forHTTPHeaderField: "mcp-session-id")
        }
        if let protoVer = protocolVersion {
            req.setValue(protoVer, forHTTPHeaderField: "mcp-protocol-version")
        }
        lock.unlock()

        var bodyDict: [String: Any] = [
            "jsonrpc": "2.0",
            "method": method,
            "params": params
        ]
        if let id = id {
            bodyDict["id"] = id
        }

        req.httpBody = try? JSONSerialization.data(withJSONObject: bodyDict)

        let (data, response) = try await transport.send(request: req)

        if response.statusCode == 401 {
            resetSession()
            throw McpClientError.unauthorized
        }
        if response.statusCode == 404 {
            resetSession()
            throw McpClientError.httpError(statusCode: 404)
        }

        // Capture session header if provided
        lock.lock()
        if let newSessionId = response.value(forHTTPHeaderField: "mcp-session-id") {
            self.sessionId = newSessionId
        }
        if let newProto = response.value(forHTTPHeaderField: "mcp-protocol-version") {
            self.protocolVersion = newProto
        }
        lock.unlock()

        // Empty response on 202 is accepted for notifications
        if (response.statusCode == 200 || response.statusCode == 202) && data.isEmpty && id == nil {
            return [:]
        }

        guard response.statusCode == 200 || response.statusCode == 202 else {
            throw McpClientError.httpError(statusCode: response.statusCode)
        }

        guard data.count <= maxResponseBytes else {
            throw McpClientError.responseTooLarge
        }

        let contentType = response.value(forHTTPHeaderField: "Content-Type") ?? ""
        let jsonObject: [String: Any]

        if contentType.contains("text/event-stream") {
            jsonObject = try parseSseResponse(data: data, expectedId: id)
        } else {
            guard let obj = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else {
                throw McpClientError.invalidResponse("Expected valid JSON response.")
            }
            jsonObject = obj
        }

        guard jsonObject["jsonrpc"] as? String == "2.0" else {
            throw McpClientError.invalidResponse("Invalid JSON-RPC version.")
        }
        if let error = jsonObject["error"] as? [String: Any] {
            let code = error["code"] as? Int ?? -1
            let message = error["message"] as? String ?? "Unknown error"
            throw McpClientError.jsonRpcError(code: code, message: message)
        }

        if let expectedId = id {
            guard let responseId = jsonObject["id"] as? Int, responseId == expectedId else {
                throw McpClientError.mismatchedId
            }
        }

        guard let result = jsonObject["result"] as? [String: Any], result["isError"] as? Bool != true else {
            throw McpClientError.invalidResponse("Tool call failed or result missing.")
        }
        return result
    }

    private func parseSseResponse(data: Data, expectedId: Int?) throws -> [String: Any] {
        guard let text = String(data: data, encoding: .utf8) else {
            throw McpClientError.invalidResponse("SSE text decoding failed.")
        }

        var accumulatedData = ""
        let lines = text.components(separatedBy: "\n")

        for line in lines {
            let trimmed = line.trimmingCharacters(in: .whitespacesAndNewlines)
            if trimmed.hasPrefix("data:") {
                let payload = trimmed.dropFirst(5).trimmingCharacters(in: .whitespaces)
                accumulatedData.append(payload)
                if accumulatedData.count > maxResponseBytes {
                    throw McpClientError.responseTooLarge
                }
            } else if trimmed.isEmpty && !accumulatedData.isEmpty {
                // End of an SSE event - check immediately without waiting for stream EOF
                if let parsedData = accumulatedData.data(using: .utf8),
                   let json = try? JSONSerialization.jsonObject(with: parsedData) as? [String: Any] {
                    if let expId = expectedId {
                        if let respId = json["id"] as? Int, respId == expId {
                            return json
                        }
                    } else {
                        return json
                    }
                }
                accumulatedData = ""
            }
        }

        if !accumulatedData.isEmpty,
           let parsedData = accumulatedData.data(using: .utf8),
           let json = try? JSONSerialization.jsonObject(with: parsedData) as? [String: Any] {
            if let expId = expectedId {
                if let respId = json["id"] as? Int, respId == expId {
                    return json
                }
            } else {
                return json
            }
        }

        throw McpClientError.invalidResponse("Could not find matching JSON-RPC object from SSE stream.")
    }
}

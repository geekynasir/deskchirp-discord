import Foundation
import Combine
import CryptoKit
import CoreGraphics

public enum ComputerAddress {
    public static func normalize(_ text: String) throws -> String {
        guard let parts = URLComponents(string: text.trimmingCharacters(in: .whitespacesAndNewlines)),
              let host = parts.host, !host.isEmpty, parts.user == nil, parts.password == nil,
              parts.query == nil, parts.fragment == nil, parts.path.isEmpty || parts.path == "/",
              parts.scheme == "https" || (parts.scheme == "http" && ["localhost", "127.0.0.1", "[::1]", "::1"].contains(host)),
              let url = parts.url else { throw ComputerLinkError.address }
        var result = url.absoluteString
        while result.hasSuffix("/") { result.removeLast() }
        return result
    }
}

public enum ComputerLinkError: LocalizedError {
    case address, connection, account, removed
    public var errorDescription: String? {
        switch self {
        case .address: return "Use a secure linking address. Plain http:// works only on this computer."
        case .connection: return "Couldn’t link this computer. Check the address and try a new code."
        case .account: return "Check that Discord uses the same account on each computer and only one copy of Developer Presence is open."
        case .removed: return "This computer’s link is no longer available. Unlink it, then use a new code."
        }
    }
}

public struct ComputerCredential: Codable, Sendable {
    public let server: String
    public let deviceId: String
    public let groupId: String
    public let token: String
}

public enum MacInputAgeReader {
    public static func seconds() -> Double? {
        let age = CGEventSource.secondsSinceLastEventType(.combinedSessionState,
            eventType: CGEventType(rawValue: UInt32.max)!)
        return age.isFinite && age >= 0 ? age : nil
    }
}

/// Read on the Discord queue as well as the main actor. No grant is persisted.
public final class ComputerPermission: @unchecked Sendable {
    private let lock = NSLock()
    private var linked = false
    private var account: String?
    private var deadline: ContinuousClock.Instant?
    private let inputAge: @Sendable () -> Double?
    public init(inputAge: @escaping @Sendable () -> Double? = { MacInputAgeReader.seconds() }) {
        self.inputAge = inputAge
    }
    public func configure(linked: Bool, account: String?) {
        lock.lock(); defer { lock.unlock() }
        if self.linked != linked || self.account != account { deadline = nil }
        self.linked = linked; self.account = account
    }
    public func grant(start: ContinuousClock.Instant, seconds: Double) {
        lock.lock(); defer { lock.unlock() }
        deadline = seconds > 3 && seconds <= 15 ? start.advanced(by: .milliseconds(Int64((seconds - 3) * 1000))) : nil
    }
    public func invalidate() { lock.lock(); deadline = nil; lock.unlock() }
    public func allows(account: String?) -> Bool {
        lock.lock(); defer { lock.unlock() }
        if !linked { return true }
        guard let deadline = deadline, ContinuousClock.now < deadline,
              let account = account, account == self.account,
              let age = inputAge(), age >= 0, age < 300 else { return false }
        return true
    }
}

@MainActor
public final class ComputerLinkService: ObservableObject {
    @Published public private(set) var status = "Not linked"
    @Published public private(set) var pairingCode = ""
    @Published public private(set) var isBusy = false
    public var onChanged: (() -> Void)?
    public nonisolated let permission: ComputerPermission
    private let keychain: KeychainManaging
    private let transport: HTTPTransporting
    private let inputAge: @Sendable () -> Double?
    private let credentialService = "com.developerpresence.computers"
    private let runtime = UUID().uuidString.replacingOccurrences(of: "-", with: "")
    private let epoch = ContinuousClock.now
    private var credential: ComputerCredential?
    private var worker: Task<Void, Never>?
    private var contextVersion = 0
    private var available = false
    private var account: String?
    private var linked = false
    private var inputSequence: Int64 = 0
    private var lastInput = -Double.infinity
    public var hasSavedLink: Bool { credential != nil }

    public init(keychain: KeychainManaging = KeychainService.shared, transport: HTTPTransporting? = nil,
                inputAge: @escaping @Sendable () -> Double? = { MacInputAgeReader.seconds() }) {
        self.keychain = keychain
        let config = URLSessionConfiguration.ephemeral
        config.timeoutIntervalForRequest = 4
        config.timeoutIntervalForResource = 4
        self.transport = transport ?? URLSessionHTTPTransport(configuration: config, maxMetadataBytes: 8192,
                                                              requestTimeout: 4, resourceTimeout: 4)
        self.inputAge = inputAge
        self.permission = ComputerPermission(inputAge: inputAge)
    }

    public func start(linked: Bool) {
        self.linked = linked
        permission.configure(linked: linked, account: nil)
        do {
            if let stored = try keychain.loadCredential(forService: credentialService, account: "device"),
               let data = stored.accessToken.data(using: .utf8) {
                let value = try JSONDecoder().decode(ComputerCredential.self, from: data)
                guard try ComputerAddress.normalize(value.server) == value.server else { throw ComputerLinkError.address }
                credential = value
            }
        } catch { status = "Couldn’t read your saved computer link. Try linking again." }
        guard worker == nil else { return }
        worker = Task { [weak self] in
            while !Task.isCancelled {
                guard let self = self else { return }
                await self.pollOnce()
                do { try await Task.sleep(for: .seconds(3)) } catch { return }
            }
        }
    }

    public func updateContext(linked: Bool, available: Bool, discordUserId: String?) {
        if self.linked != linked || self.available != available || self.account != discordUserId {
            contextVersion += 1
            permission.invalidate()
        }
        self.linked = linked; self.available = available; account = discordUserId
        permission.configure(linked: linked, account: available ? discordUserId : nil)
    }

    /// The coordinator persists linked mode first, before invoking this request.
    public func link(address: String, code: String, name: String) async -> Bool {
        guard !isBusy, credential == nil else { return false }
        isBusy = true; defer { isBusy = false }
        contextVersion += 1; permission.invalidate()
        do {
            let server = try ComputerAddress.normalize(address)
            let result = try await post(server: server, path: "/v1/join", body: ["code": code, "name": name])
            guard let device = result["deviceId"] as? String, let group = result["groupId"] as? String,
                  let token = result["token"] as? String else { throw ComputerLinkError.connection }
            let value = ComputerCredential(server: server, deviceId: device, groupId: group, token: token)
            let data = try JSONEncoder().encode(value)
            try keychain.saveCredential(T3StoredCredential(accessToken: String(decoding: data, as: UTF8.self)),
                                        forService: credentialService, account: "device")
            credential = value
            status = "Linked. Open Discord and turn on sharing when you’re ready."
            onChanged?(); return true
        } catch {
            status = (error as? ComputerLinkError)?.errorDescription ?? "Couldn’t link this computer. Check the address and try a new code."
            onChanged?(); return false
        }
    }

    public func createCode() async {
        guard !isBusy, let credential = credential else { return }
        isBusy = true; defer { isBusy = false }
        do {
            let result = try await post(server: credential.server, path: "/v1/pair-code", body: [:], token: credential.token)
            pairingCode = result["code"] as? String ?? ""
            status = "Enter this code on your other computer within five minutes. It works once."
        } catch { status = "Couldn’t create a linking code. Check the connection and try again." }
        onChanged?()
    }

    public func unlink() async -> Bool {
        guard !isBusy else { return false }
        isBusy = true; defer { isBusy = false }
        contextVersion += 1; permission.invalidate()
        do {
            if let value = credential {
                do { _ = try await post(server: value.server, path: "/v1/leave", body: [:], token: value.token) }
                catch ComputerLinkError.removed { /* Already revoked. Remove the local credential too. */ }
            }
            try keychain.deleteCredential(forService: credentialService, account: "device")
            credential = nil; pairingCode = ""
            status = "Not linked. Sharing is off on this computer."
            onChanged?(); return true
        } catch {
            status = "Couldn’t unlink this computer. Sharing is off; check the connection and try again."
            onChanged?(); return false
        }
    }

    public func pollOnce() async {
        guard linked, !isBusy else { return }
        guard let value = credential else {
            if status == "Not linked" { status = "Not linked. Enter a linking code to continue."; onChanged?() }
            return
        }
        let input = inputAge()
        let age = input.flatMap { $0.isFinite && $0 >= 0 && $0 <= 604800 ? $0 : nil } ?? 604800
        let instant = seconds(epoch.duration(to: .now)) - age
        if instant > lastInput + 0.3 { lastInput = instant; inputSequence += 1 }
        let version = contextVersion
        let currentAccount = account
        let canParticipate = available && input != nil && currentAccount != nil
        let accountKey = currentAccount.map {
            SHA256.hash(data: Data((value.groupId + ":" + $0).utf8)).map { String(format: "%02x", $0) }.joined()
        } ?? ""
        let start = ContinuousClock.now
        do {
            let result = try await post(server: value.server, path: "/v1/heartbeat", body: [
                "runtimeId": runtime, "inputSequence": inputSequence, "inputAge": floor(age),
                "available": canParticipate, "accountKey": accountKey
            ], token: value.token)
            guard version == contextVersion, linked, !isBusy else { return }
            guard let validity = result["validFor"] as? Double, let allowed = result["allowed"] as? Bool else {
                throw ComputerLinkError.connection
            }
            if allowed && canParticipate { permission.grant(start: start, seconds: validity) }
            else { permission.invalidate() }
            let selected = result["selectedName"] as? String
            status = permission.allows(account: account) ? "Selected computer: this computer"
                : result["switching"] as? Bool == true ? "Switching computers…"
                : selected.map { "Selected computer: \($0)" } ?? "No computer is sharing"
        } catch {
            guard version == contextVersion else { return }
            permission.invalidate()
            status = (error as? ComputerLinkError)?.errorDescription ?? "Waiting for a connection"
        }
        onChanged?()
    }

    public func stop() {
        contextVersion += 1; available = false; permission.invalidate()
        worker?.cancel(); worker = nil
    }

    private func post(server: String, path: String, body: [String: Any], token: String? = nil) async throws -> [String: Any] {
        let address = try ComputerAddress.normalize(server)
        guard let url = URL(string: address + path) else { throw ComputerLinkError.address }
        var request = URLRequest(url: url, timeoutInterval: 4)
        request.httpMethod = "POST"
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        if let token = token { request.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization") }
        request.httpBody = try JSONSerialization.data(withJSONObject: body)
        let (data, response) = try await transport.send(request: request)
        if response.statusCode == 401 { throw ComputerLinkError.removed }
        if response.statusCode == 409 { throw ComputerLinkError.account }
        guard response.statusCode == 200, data.count <= 8192,
              let result = try JSONSerialization.jsonObject(with: data) as? [String: Any] else { throw ComputerLinkError.connection }
        return result
    }

    private func seconds(_ duration: Duration) -> Double {
        Double(duration.components.seconds) + Double(duration.components.attoseconds) / 1e18
    }
}

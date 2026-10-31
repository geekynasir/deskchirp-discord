import Foundation
#if canImport(Security)
import Security
#endif

/// Stored OAuth credential. Contains access token and optional expiration date.
public struct T3StoredCredential: Codable, Equatable, Sendable {
    public let accessToken: String
    public let expiresAt: Date?

    public init(accessToken: String, expiresAt: Date? = nil) {
        self.accessToken = accessToken
        self.expiresAt = expiresAt
    }

    public func isExpired(at date: Date = Date()) -> Bool {
        if let expiresAt = expiresAt {
            return date >= expiresAt
        }
        return false
    }
}

public protocol KeychainManaging: Sendable {
    func saveCredential(_ credential: T3StoredCredential, forService service: String, account: String) throws
    func loadCredential(forService service: String, account: String) throws -> T3StoredCredential?
    func deleteCredential(forService service: String, account: String) throws
}

public enum KeychainError: LocalizedError, Equatable {
    case saveFailed(status: Int32)
    case readFailed(status: Int32)
    case deleteFailed(status: Int32)
    case encodingFailed

    public var errorDescription: String? {
        switch self {
        case .saveFailed, .readFailed, .deleteFailed, .encodingFailed:
            return "Couldn’t save your sign-in to Keychain. Try connecting again."
        }
    }
}

/// Native macOS Keychain service using Security framework.
public final class KeychainService: KeychainManaging, @unchecked Sendable {
    public static let shared = KeychainService()
    public static let defaultService = "com.developerpresence.t3"

    public init() {}

    public func saveCredential(_ credential: T3StoredCredential, forService service: String, account: String) throws {
        guard let data = try? JSONEncoder().encode(credential) else {
            throw KeychainError.encodingFailed
        }

        #if canImport(Security)
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account
        ]

        let attributesToUpdate: [String: Any] = [
            kSecValueData as String: data,
            kSecAttrAccessible as String: kSecAttrAccessibleAfterFirstUnlock
        ]

        let updateStatus = SecItemUpdate(query as CFDictionary, attributesToUpdate as CFDictionary)
        if updateStatus == errSecSuccess {
            return
        } else if updateStatus == errSecItemNotFound {
            var addQuery = query
            addQuery[kSecValueData as String] = data
            addQuery[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlock
            let addStatus = SecItemAdd(addQuery as CFDictionary, nil)
            guard addStatus == errSecSuccess else {
                throw KeychainError.saveFailed(status: addStatus)
            }
        } else {
            throw KeychainError.saveFailed(status: updateStatus)
        }
        #endif
    }

    public func loadCredential(forService service: String, account: String) throws -> T3StoredCredential? {
        #if canImport(Security)
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
            kSecReturnData as String: kCFBooleanTrue as Any,
            kSecMatchLimit as String: kSecMatchLimitOne
        ]

        var item: CFTypeRef?
        let status = SecItemCopyMatching(query as CFDictionary, &item)

        if status == errSecItemNotFound {
            return nil
        }
        guard status == errSecSuccess, let data = item as? Data else {
            throw KeychainError.readFailed(status: status)
        }

        return try? JSONDecoder().decode(T3StoredCredential.self, from: data)
        #else
        return nil
        #endif
    }

    public func deleteCredential(forService service: String, account: String) throws {
        #if canImport(Security)
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account
        ]

        let status = SecItemDelete(query as CFDictionary)
        if status != errSecSuccess && status != errSecItemNotFound {
            throw KeychainError.deleteFailed(status: status)
        }
        #endif
    }
}

/// In-memory mock keychain for testing and environments without Security framework.
public final class MockKeychain: KeychainManaging, @unchecked Sendable {
    private let lock = NSLock()
    private var storage: [String: T3StoredCredential] = [:]
    public var errorToThrowOnSave: Error?

    public init() {}

    private func key(service: String, account: String) -> String {
        "\(service):\(account)"
    }

    public func saveCredential(_ credential: T3StoredCredential, forService service: String, account: String) throws {
        lock.lock()
        defer { lock.unlock() }
        if let error = errorToThrowOnSave {
            throw error
        }
        storage[key(service: service, account: account)] = credential
    }

    public func loadCredential(forService service: String, account: String) throws -> T3StoredCredential? {
        lock.lock()
        defer { lock.unlock() }
        return storage[key(service: service, account: account)]
    }

    public func deleteCredential(forService service: String, account: String) throws {
        lock.lock()
        defer { lock.unlock() }
        storage.removeValue(forKey: key(service: service, account: account))
    }

    public func clear() {
        lock.lock()
        defer { lock.unlock() }
        storage.removeAll()
    }
}

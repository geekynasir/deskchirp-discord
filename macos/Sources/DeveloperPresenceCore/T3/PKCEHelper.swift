import Foundation
#if canImport(CryptoKit)
import CryptoKit
#endif

extension Data {
    /// Encodes data to base64url string without padding per RFC 7636 / RFC 4648.
    public func base64URLEncodedString() -> String {
        base64EncodedString()
            .replacingOccurrences(of: "+", with: "-")
            .replacingOccurrences(of: "/", with: "_")
            .replacingOccurrences(of: "=", with: "")
    }
}

/// Helper for OAuth 2.0 PKCE (RFC 7636) code verifier, S256 code challenge, and state generation.
public enum PKCEHelper {
    /// Generates a cryptographically secure random code verifier.
    /// Length: 64 characters, unreserved URL-safe characters [A-Za-z0-9-_].
    public static func generateCodeVerifier() -> String {
        var generator = SystemRandomNumberGenerator()
        let bytes = (0..<48).map { _ in UInt8.random(in: 0...255, using: &generator) }
        return Data(bytes).base64URLEncodedString()
    }

    /// Computes S256 code challenge from a code verifier.
    public static func generateCodeChallenge(for verifier: String) -> String {
        guard let data = verifier.data(using: .utf8) else {
            return ""
        }
        #if canImport(CryptoKit)
        let hash = SHA256.hash(data: data)
        return Data(hash).base64URLEncodedString()
        #else
        // Fallback using CommonCrypto if CryptoKit is unavailable
        var digest = [UInt8](repeating: 0, count: 32)
        data.withUnsafeBytes {
            _ = CC_SHA256($0.baseAddress, CC_LONG(data.count), &digest)
        }
        return Data(digest).base64URLEncodedString()
        #endif
    }

    /// Generates a cryptographically secure random state parameter for CSRF mitigation.
    public static func generateState() -> String {
        var generator = SystemRandomNumberGenerator()
        let bytes = (0..<32).map { _ in UInt8.random(in: 0...255, using: &generator) }
        return Data(bytes).base64URLEncodedString()
    }
}

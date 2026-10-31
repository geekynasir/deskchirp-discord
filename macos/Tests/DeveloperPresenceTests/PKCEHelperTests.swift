import XCTest
@testable import DeveloperPresenceCore

final class PKCEHelperTests: XCTestCase {
    func testCodeVerifierSpecificationCompliance() {
        let verifier = PKCEHelper.generateCodeVerifier()

        // RFC 7636 Section 4.1: code_verifier length between 43 and 128 characters
        XCTAssertGreaterThanOrEqual(verifier.count, 43)
        XCTAssertLessThanOrEqual(verifier.count, 128)

        // Must contain only unreserved URL characters [A-Za-z0-9-._~]
        let allowed = CharacterSet(charactersIn: "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~")
        XCTAssertTrue(verifier.unicodeScalars.allSatisfy { allowed.contains($0) })
    }

    func testCodeChallengeMatchesKnownVector() {
        // Known test vector from T3 reference tests and RFC 7636 test suites
        let knownVerifier = String(repeating: "a", count: 43) + "-verifier-for-tests"
        let expectedChallenge = "DeB41nTVkPwpbbYecrnqtVq7VXLezustdHAK4SWt13c"

        let computedChallenge = PKCEHelper.generateCodeChallenge(for: knownVerifier)
        XCTAssertEqual(computedChallenge, expectedChallenge)
    }

    func testStateGeneration() {
        let state1 = PKCEHelper.generateState()
        let state2 = PKCEHelper.generateState()

        XCTAssertFalse(state1.isEmpty)
        XCTAssertFalse(state2.isEmpty)
        XCTAssertNotEqual(state1, state2, "Successive random states must not collide")

        // No padding '=' in base64url strings
        XCTAssertFalse(state1.contains("="))
        XCTAssertFalse(state1.contains("+"))
        XCTAssertFalse(state1.contains("/"))
    }
}

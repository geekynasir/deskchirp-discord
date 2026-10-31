import XCTest
@testable import DeveloperPresenceCore

final class DiscordArtworkTests: XCTestCase {
    func testOnlyFixedPublicImagesAreAllowedAndOptOutWorks() {
        XCTAssertNil(DiscordArtwork.url(for: "https://private.example/secret"))
        let activity = DiscordActivity(details: "Using Codex")
        XCTAssertNil(DiscordArtwork.apply(to: activity, app: .codex, enabled: false)?.assets)
        XCTAssertEqual(DiscordArtwork.apply(to: activity, app: .codex, enabled: true)?.assets?.large_image, DiscordArtwork.baseURL + "harness_codex.png")
    }

    func testT3HasAppImageAndSelectedToolBadge() {
        let activity = DiscordActivity(details: "T3 Code · Grok", assets: DiscordActivityAssets(largeImage: "t3_code", smallImage: "harness_grok"))
        let result = DiscordArtwork.apply(to: activity, app: .t3Code, enabled: true)
        XCTAssertEqual(result?.assets?.large_image, DiscordArtwork.baseURL + "t3_code.png")
        XCTAssertEqual(result?.assets?.small_image, DiscordArtwork.baseURL + "harness_grok.png")
    }
}

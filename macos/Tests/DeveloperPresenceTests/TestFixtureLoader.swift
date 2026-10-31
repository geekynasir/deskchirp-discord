import Foundation
import XCTest

enum TestFixtureLoader {
    static func loadResource(named name: String) throws -> Data {
        // 1. Try Bundle.module if available
        #if SWIFT_PACKAGE
        if let bundleUrl = Bundle.module.url(forResource: name, withExtension: "json"),
           let data = try? Data(contentsOf: bundleUrl) {
            return data
        }
        #endif

        // 2. Try relative paths from current file / working directory
        let searchPaths = [
            "Tests/DeveloperPresenceTests/Resources/\(name).json",
            "Resources/\(name).json",
            "../spec/\(name).json",
            "../../spec/\(name).json",
            "/data/ubuntu/projects/developer-presence/spec/\(name).json"
        ]

        for path in searchPaths {
            let url = URL(fileURLWithPath: path)
            if let data = try? Data(contentsOf: url) {
                return data
            }
        }

        throw NSError(domain: "TestFixtureLoader", code: 404, userInfo: [
            NSLocalizedDescriptionKey: "Could not locate test fixture '\(name).json'."
        ])
    }
}

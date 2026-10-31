// swift-tools-version: 5.9
import PackageDescription

let package = Package(
    name: "DeveloperPresence",
    platforms: [
        .macOS(.v13)
    ],
    products: [
        .executable(
            name: "DeveloperPresence",
            targets: ["DeveloperPresence"]
        ),
        .library(
            name: "DeveloperPresenceCore",
            targets: ["DeveloperPresenceCore"]
        )
    ],
    dependencies: [],
    targets: [
        .target(
            name: "DeveloperPresenceCore",
            dependencies: [],
            path: "Sources/DeveloperPresenceCore"
        ),
        .executableTarget(
            name: "DeveloperPresence",
            dependencies: ["DeveloperPresenceCore"],
            path: "Sources/DeveloperPresence",
            exclude: ["Info.plist"]
        ),
        .testTarget(
            name: "DeveloperPresenceTests",
            dependencies: ["DeveloperPresenceCore"],
            path: "Tests/DeveloperPresenceTests",
            resources: [
                .process("Resources")
            ]
        )
    ]
)

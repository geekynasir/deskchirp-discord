import Foundation
#if canImport(AppKit)
import AppKit
#endif

public protocol BrowserOpening: Sendable {
    func openURL(_ url: URL) -> Bool
}

public final class WorkspaceBrowserOpener: BrowserOpening, @unchecked Sendable {
    public static let shared = WorkspaceBrowserOpener()

    public init() {}

    public func openURL(_ url: URL) -> Bool {
        #if canImport(AppKit)
        return NSWorkspace.shared.open(url)
        #else
        return false
        #endif
    }
}

public final class MockBrowserOpener: BrowserOpening, @unchecked Sendable {
    private let lock = NSLock()
    public private(set) var openedURLs: [URL] = []
    public var shouldSucceed: Bool = true

    public init() {}

    public func openURL(_ url: URL) -> Bool {
        lock.lock()
        defer { lock.unlock() }
        openedURLs.append(url)
        return shouldSucceed
    }

    public var lastOpenedURL: URL? {
        lock.lock()
        defer { lock.unlock() }
        return openedURLs.last
    }
}

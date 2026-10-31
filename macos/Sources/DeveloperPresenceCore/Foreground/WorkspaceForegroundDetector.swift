import Foundation
#if canImport(AppKit)
import AppKit
#endif

/// Detects foreground application using macOS NSWorkspace.
///
/// Privacy & Security Guarantees:
/// - Reads ONLY `NSWorkspace.shared.frontmostApplication?.bundleIdentifier`.
/// - Never inspects window titles, document URLs, screen pixels, or process arguments.
/// - Requires NO Screen Recording or Accessibility permissions.
/// - Fails closed: unmapped or unknown applications return `SupportedApp.none`.
public final class WorkspaceForegroundDetector: ForegroundDetecting, @unchecked Sendable {
    private let mappingsLock = NSLock()
    private var notificationObserver: AnyObject?
    private var bundleMappings: [String: SupportedApp]

    public var onForegroundAppChanged: (@Sendable (SupportedApp, String?) -> Void)?

    public init(bundleMappings: [String: SupportedApp] = PresenceSettings.defaultBundleMappings) {
        var normalized: [String: SupportedApp] = [:]
        for (key, value) in bundleMappings {
            normalized[key.lowercased().trimmingCharacters(in: .whitespacesAndNewlines)] = value
        }
        self.bundleMappings = normalized
    }

    public func updateBundleMappings(_ mappings: [String: SupportedApp]) {
        var normalized: [String: SupportedApp] = [:]
        for (key, value) in mappings {
            normalized[key.lowercased().trimmingCharacters(in: .whitespacesAndNewlines)] = value
        }
        mappingsLock.lock()
        bundleMappings = normalized
        mappingsLock.unlock()
        DispatchQueue.main.async { [weak self] in
            guard let self else { return }
            let (app, bundleId) = self.currentForegroundApp()
            self.onForegroundAppChanged?(app, bundleId)
        }
    }

    public func start() {
        stop()

        #if canImport(AppKit)
        let center = NSWorkspace.shared.notificationCenter
        notificationObserver = center.addObserver(
            forName: NSWorkspace.didActivateApplicationNotification,
            object: nil,
            queue: .main
        ) { [weak self] _ in
            guard let self = self else { return }
            let (app, bundleId) = self.currentForegroundApp()
            self.onForegroundAppChanged?(app, bundleId)
        }

        // Initial immediate refresh
        DispatchQueue.main.async { [weak self] in
            guard let self = self else { return }
            let (app, bundleId) = self.currentForegroundApp()
            self.onForegroundAppChanged?(app, bundleId)
        }
        #endif
    }

    public func stop() {
        #if canImport(AppKit)
        if let observer = notificationObserver {
            NSWorkspace.shared.notificationCenter.removeObserver(observer)
            notificationObserver = nil
        }
        #endif
    }

    public func currentForegroundApp() -> (SupportedApp, String?) {
        #if canImport(AppKit)
        // Must be queried safely on main thread if needed
        let frontApp: NSRunningApplication?
        if Thread.isMainThread {
            frontApp = NSWorkspace.shared.frontmostApplication
        } else {
            frontApp = DispatchQueue.main.sync {
                NSWorkspace.shared.frontmostApplication
            }
        }

        guard let bundleId = frontApp?.bundleIdentifier else {
            return (.none, nil)
        }

        let normalizedBundleId = bundleId.lowercased().trimmingCharacters(in: .whitespacesAndNewlines)
        mappingsLock.lock()
        let matchedApp = bundleMappings[normalizedBundleId] ?? .none
        mappingsLock.unlock()
        return (matchedApp, bundleId)
        #else
        return (.none, nil)
        #endif
    }
}

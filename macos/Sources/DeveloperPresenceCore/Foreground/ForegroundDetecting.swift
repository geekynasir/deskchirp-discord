import Foundation

/// Interface for detecting the active foreground application on macOS.
public protocol ForegroundDetecting: AnyObject, Sendable {
    var onForegroundAppChanged: (@Sendable (SupportedApp, String?) -> Void)? { get set }

    func start()
    func stop()
    func updateBundleMappings(_ mappings: [String: SupportedApp])
    func isAppRunning(_ app: SupportedApp) -> Bool
    func currentForegroundApp() -> (SupportedApp, String?)
}

public extension ForegroundDetecting {
    func isAppRunning(_ app: SupportedApp) -> Bool { currentForegroundApp().0 == app }
}

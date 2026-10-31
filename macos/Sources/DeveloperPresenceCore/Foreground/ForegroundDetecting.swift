import Foundation

/// Interface for detecting the active foreground application on macOS.
public protocol ForegroundDetecting: AnyObject, Sendable {
    var onForegroundAppChanged: (@Sendable (SupportedApp, String?) -> Void)? { get set }

    func start()
    func stop()
    func updateBundleMappings(_ mappings: [String: SupportedApp])
    func currentForegroundApp() -> (SupportedApp, String?)
}

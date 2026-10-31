import Foundation

/// Interface for observing macOS session state (sleep, wake, lock, unlock).
public protocol SessionStateObserving: AnyObject, Sendable {
    var isSuspended: Bool { get }
    var onSessionStateChanged: (@Sendable (Bool) -> Void)? { get set }

    func start()
    func stop()
}

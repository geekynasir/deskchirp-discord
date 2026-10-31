import Foundation
import AppKit

/// Uses public workspace lifecycle events and distributed lock notifications.
/// The lock notification names are not public SDK constants; verify them on
/// supported macOS releases before claiming reliable lock handling.
public final class WorkspaceSessionObserver: SessionStateObserving, @unchecked Sendable {
    private enum Gate: Hashable { case sleep, displaySleep, locked, inactiveSession }
    private let lock = NSLock()
    private var gates: Set<Gate> = []
    private var workspaceObservers: [NSObjectProtocol] = []
    private var distributedObservers: [NSObjectProtocol] = []
    public var onSessionStateChanged: (@Sendable (Bool) -> Void)?

    public init() {}

    public var isSuspended: Bool {
        lock.lock()
        defer { lock.unlock() }
        return !gates.isEmpty
    }

    public func start() {
        stop()
        let center = NSWorkspace.shared.notificationCenter
        let events: [(Notification.Name, Gate, Bool)] = [
            (NSWorkspace.willSleepNotification, .sleep, true),
            (NSWorkspace.didWakeNotification, .sleep, false),
            (NSWorkspace.screensDidSleepNotification, .displaySleep, true),
            (NSWorkspace.screensDidWakeNotification, .displaySleep, false),
            (NSWorkspace.sessionDidResignActiveNotification, .inactiveSession, true),
            (NSWorkspace.sessionDidBecomeActiveNotification, .inactiveSession, false)
        ]
        workspaceObservers = events.map { name, gate, suspended in
            center.addObserver(forName: name, object: nil, queue: .main) { [weak self] _ in
                self?.update(gate, suspended: suspended)
            }
        }
        let distributed = DistributedNotificationCenter.default()
        distributedObservers = [
            distributed.addObserver(forName: Notification.Name("com.apple.screenIsLocked"), object: nil, queue: .main) { [weak self] _ in
                self?.update(.locked, suspended: true)
            },
            distributed.addObserver(forName: Notification.Name("com.apple.screenIsUnlocked"), object: nil, queue: .main) { [weak self] _ in
                self?.update(.locked, suspended: false)
            }
        ]
    }

    public func stop() {
        let center = NSWorkspace.shared.notificationCenter
        workspaceObservers.forEach { center.removeObserver($0) }
        workspaceObservers.removeAll()
        let distributed = DistributedNotificationCenter.default()
        distributedObservers.forEach { distributed.removeObserver($0) }
        distributedObservers.removeAll()
    }

    private func update(_ gate: Gate, suspended: Bool) {
        lock.lock()
        let previous = !gates.isEmpty
        if suspended { gates.insert(gate) } else { gates.remove(gate) }
        let current = !gates.isEmpty
        lock.unlock()
        // Waking the display must not remove a separate screen-lock gate.
        if previous != current { onSessionStateChanged?(current) }
    }
}

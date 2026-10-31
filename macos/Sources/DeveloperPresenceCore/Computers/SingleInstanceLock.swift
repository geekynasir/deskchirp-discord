import Foundation
import Darwin

/// Per-user process lock. Closing the descriptor or exiting releases it.
public final class SingleInstanceLock {
    private var descriptor: Int32 = -1
    public init() {}
    public func acquire() -> Bool {
        if descriptor >= 0 { return true }
        guard let support = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first else { return false }
        let directory = support.appendingPathComponent("DeveloperPresence", isDirectory: true)
        do { try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true,
                                                     attributes: [.posixPermissions: 0o700]) }
        catch { return false }
        let fd = open(directory.appendingPathComponent("instance.lock").path, O_CREAT | O_RDWR | O_CLOEXEC, 0o600)
        guard fd >= 0 else { return false }
        guard flock(fd, LOCK_EX | LOCK_NB) == 0 else { close(fd); return false }
        descriptor = fd
        return true
    }
    deinit { if descriptor >= 0 { close(descriptor) } }
}

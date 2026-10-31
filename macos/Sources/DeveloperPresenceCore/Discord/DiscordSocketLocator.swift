import Foundation

/// Locates active Discord IPC UNIX domain socket on macOS.
/// Sockets are named `discord-ipc-0` through `discord-ipc-9`.
public enum DiscordSocketLocator {
    /// Generates candidate socket paths across search directories in order.
    public static func candidateSocketPaths() -> [String] {
        var directories: [String] = []

        if let tmpdir = ProcessInfo.processInfo.environment["TMPDIR"], !tmpdir.isEmpty {
            directories.append(tmpdir)
        }

        let nsTemp = NSTemporaryDirectory()
        if !nsTemp.isEmpty {
            directories.append(nsTemp)
        }

        if let xdg = ProcessInfo.processInfo.environment["XDG_RUNTIME_DIR"], !xdg.isEmpty {
            directories.append(xdg)
        }
        if let tmp = ProcessInfo.processInfo.environment["TMP"], !tmp.isEmpty {
            directories.append(tmp)
        }
        if let temp = ProcessInfo.processInfo.environment["TEMP"], !temp.isEmpty {
            directories.append(temp)
        }
        directories.append("/tmp")

        var paths: [String] = []
        var seenDirs = Set<String>()

        for dir in directories {
            let normalized = dir.hasSuffix("/") ? String(dir.dropLast()) : dir
            guard !normalized.isEmpty, seenDirs.insert(normalized).inserted else {
                continue
            }
            for i in 0...9 {
                paths.append("\(normalized)/discord-ipc-\(i)")
            }
        }

        return paths
    }

    /// Returns all candidate socket paths that currently exist on the file system.
    public static func existingSocketPaths(fileManager: FileManager = .default) -> [String] {
        let candidates = candidateSocketPaths()
        var existing: [String] = []
        for path in candidates {
            var isDir: ObjCBool = false
            if fileManager.fileExists(atPath: path, isDirectory: &isDir), !isDir.boolValue {
                existing.append(path)
            }
        }
        return existing
    }

    /// Returns candidate socket paths ordered by existing socket files first,
    /// followed by remaining unverified candidates.
    public static func prioritizedCandidatePaths(fileManager: FileManager = .default) -> [String] {
        let existing = existingSocketPaths(fileManager: fileManager)
        let all = candidateSocketPaths()
        var result = existing
        for path in all where !result.contains(path) {
            result.append(path)
        }
        return result
    }

    /// Finds the first existing socket path, or returns the primary default candidate.
    public static func findActiveSocketPath() -> String? {
        let existing = existingSocketPaths()
        return existing.first ?? candidateSocketPaths().first
    }
}

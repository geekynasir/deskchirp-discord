import SwiftUI

/// Menu dropdown contents displayed by MenuBarExtra.
@MainActor
public struct MenuBarContentView: View {
    @ObservedObject public var coordinator: PresenceCoordinator

    public init(coordinator: PresenceCoordinator) {
        self.coordinator = coordinator
    }

    public var body: some View {
        Group {
            // Status and Foreground Display
            Text("Developer Presence")
                .font(.headline)

            Text("Status: \(coordinator.state.statusMessage)")

            if coordinator.state.foregroundApp != .none {
                Text("Foreground: \(coordinator.state.foregroundApp.displayName)")
            } else if let bundleId = coordinator.state.detectedBundleId {
                Text("Foreground: Unmapped (\(bundleId))")
            } else {
                Text("Foreground: None")
            }

            Text("Preview: \(coordinator.state.outgoingHeadline)")

            Divider()

            // Actions
            Button(coordinator.settings.enableSharing ? "Disable Presence Sharing" : "Enable Presence Sharing") {
                coordinator.toggleSharing()
            }

            Button(coordinator.settings.isPaused ? "Resume Sharing" : "Pause Sharing") {
                coordinator.togglePause()
            }
            .disabled(!coordinator.settings.enableSharing)

            Divider()

            Button("Settings…") {
                #if canImport(AppKit)
                SettingsWindowController.shared.showSettings(coordinator: coordinator)
                #endif
            }
            .keyboardShortcut(",", modifiers: [.command])

            Divider()

            Button("Quit Developer Presence") {
                coordinator.quitApplication()
            }
            .keyboardShortcut("q", modifiers: [.command])
        }
    }
}

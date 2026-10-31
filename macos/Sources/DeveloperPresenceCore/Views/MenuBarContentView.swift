import SwiftUI

/// Menu dropdown contents displayed by MenuBarExtra matching product language specification.
@MainActor
public struct MenuBarContentView: View {
    @ObservedObject public var coordinator: PresenceCoordinator

    public init(coordinator: PresenceCoordinator) {
        self.coordinator = coordinator
    }

    public var body: some View {
        Group {
            Text("Developer Presence")
                .font(.headline)

            Text(UserFacingStatus.describe(state: coordinator.state, settings: coordinator.settings).title)

            if coordinator.state.outgoingHeadline != ActivityFormatter.headlineNone {
                Text(coordinator.state.outgoingHeadline)
                if let state = coordinator.state.outgoingState {
                    Text(state)
                }
            }

            Divider()

            Toggle("Share on Discord", isOn: Binding(
                get: { coordinator.settings.enableSharing },
                set: { value in
                    if value != coordinator.settings.enableSharing { coordinator.toggleSharing() }
                }
            ))

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

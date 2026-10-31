import SwiftUI

@MainActor
public struct SettingsView: View {
    @ObservedObject public var coordinator: PresenceCoordinator
    @State private var newBundleId = ""
    @State private var selectedApp: SupportedApp = .t3Code

    public init(coordinator: PresenceCoordinator) {
        self.coordinator = coordinator
    }

    public var body: some View {
        TabView {
            Form {
                Section {
                    Toggle("Share activity on Discord", isOn: Binding(
                        get: { coordinator.settings.enableSharing },
                        set: { value in
                            if value != coordinator.settings.enableSharing { coordinator.toggleSharing() }
                        }
                    ))
                    Toggle("Pause sharing", isOn: Binding(
                        get: { coordinator.settings.isPaused },
                        set: { value in
                            if value != coordinator.settings.isPaused { coordinator.togglePause() }
                        }
                    ))
                }
                Section("Discord") {
                    TextField("Application ID", text: Binding(
                        get: { coordinator.settings.discordClientId },
                        set: { coordinator.setDiscordClientId($0) }
                    ))
                    if !coordinator.settings.discordClientId.isEmpty && !coordinator.settings.isValidDiscordClientId {
                        Label("Enter a public ID with 15–20 digits.", systemImage: "exclamationmark.circle")
                            .foregroundStyle(.secondary)
                    }
                    Link("Open Discord Developer Portal", destination: URL(string: "https://discord.com/developers/applications")!)
                }
                Section("Outgoing preview") {
                    Text(coordinator.state.outgoingHeadline).font(.headline)
                    Text(coordinator.state.statusMessage).foregroundStyle(.secondary)
                    Text("Foreground: \(coordinator.state.foregroundApp.displayName)")
                    if let updated = coordinator.state.lastUpdate {
                        Text("Last acknowledged: \(updated.formatted(date: .omitted, time: .standard))")
                            .font(.caption).foregroundStyle(.secondary)
                    }
                }
                Section {
                    Text("Shares app focus only. Conversation text, window titles, and task counts aren't shared.")
                        .foregroundStyle(.secondary)
                }
            }
            .formStyle(.grouped)
            .tabItem { Label("General", systemImage: "gearshape") }

            VStack(alignment: .leading, spacing: 12) {
                Text("Add a bundle identifier if your app isn't detected.")
                if let bundleId = coordinator.state.detectedBundleId {
                    Text("Current app: \(bundleId)").font(.caption).textSelection(.enabled)
                }
                List {
                    ForEach(coordinator.settings.bundleMappings.keys.sorted(), id: \.self) { id in
                        HStack {
                            VStack(alignment: .leading) {
                                Text(coordinator.settings.bundleMappings[id]?.displayName ?? "Unknown")
                                Text(id).font(.caption).foregroundStyle(.secondary)
                            }
                            Spacer()
                            Button(role: .destructive) {
                                coordinator.removeBundleMapping(bundleId: id)
                            } label: {
                                Image(systemName: "trash")
                            }
                            .buttonStyle(.borderless)
                            .accessibilityLabel("Remove mapping for \(id)")
                        }
                    }
                }
                TextField("Bundle identifier", text: $newBundleId)
                    .textFieldStyle(.roundedBorder)
                    .accessibilityLabel("Bundle identifier")
                HStack {
                    Picker("Application", selection: $selectedApp) {
                        Text("T3 Code").tag(SupportedApp.t3Code)
                        Text("Claude Desktop").tag(SupportedApp.claude)
                        Text("Codex Desktop").tag(SupportedApp.codex)
                    }
                    Button("Add") {
                        coordinator.updateBundleMapping(bundleId: newBundleId, app: selectedApp)
                        newBundleId = ""
                    }
                    .disabled(newBundleId.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
                }
                Button("Restore defaults") { coordinator.resetBundleMappingsToDefaults() }
            }
            .padding(20)
            .tabItem { Label("Applications", systemImage: "app.badge") }
        }
        .frame(minWidth: 520, minHeight: 520)
    }
}

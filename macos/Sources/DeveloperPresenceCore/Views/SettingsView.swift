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
            generalTab
                .tabItem { Label("General", systemImage: "gearshape") }

            appDetectionTab
                .tabItem { Label("App Detection", systemImage: "app.badge") }
        }
        .frame(minWidth: 540, minHeight: 560)
    }

    // MARK: - General Tab

    private var generalTab: some View {
        Form {
            // 1. Sharing switches
            Section {
                Toggle("Share on Discord", isOn: Binding(
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
                .disabled(!coordinator.settings.enableSharing)
            }

            // 2. Discord Preview
            Section("Discord preview") {
                VStack(alignment: .leading, spacing: 4) {
                    Text(previewHeadline)
                        .font(.headline)

                    if let state = coordinator.state.outgoingState {
                        Text(state)
                            .font(.body)
                    }

                    Text(sharingStatus.title)
                        .font(.subheadline)
                        .foregroundStyle(.secondary)

                    if let recovery = sharingStatus.recovery {
                        Text(recovery)
                            .font(.caption)
                            .foregroundStyle(.secondary)
                            .fixedSize(horizontal: false, vertical: true)
                    }

                    if coordinator.state.foregroundApp == .t3Code, let note = coordinator.state.t3ActivityNote {
                        Text(note)
                            .font(.caption)
                            .foregroundStyle(.secondary)
                            .fixedSize(horizontal: false, vertical: true)
                    }

                    if let updated = coordinator.state.lastUpdate {
                        Text("Last updated \(updated.formatted(date: .omitted, time: .shortened))")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    }
                }
                .accessibilityElement(children: .combine)
                .accessibilityLabel(previewAccessibilityLabel)
            }

            // 3. T3 Code section
            Section("T3 Code") {
                VStack(alignment: .leading, spacing: 6) {
                    Toggle("Show T3 activity", isOn: Binding(
                        get: { coordinator.settings.showT3Activity },
                        set: { value in
                            if value != coordinator.settings.showT3Activity { coordinator.toggleT3Activity() }
                        }
                    ))

                    Text("While T3 Code is the active app, also show which agent tool is busiest and how many tasks and helpers it’s running. A helper is an extra agent that a task starts to handle part of its work. Each T3 thread counts as a task.")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                }

                VStack(alignment: .leading, spacing: 6) {
                    TextField("T3 address", text: Binding(
                        get: { coordinator.settings.t3Address },
                        set: { coordinator.setT3Address($0) }
                    ), prompt: Text("Paste from T3 Code"))
                    .textFieldStyle(.roundedBorder)
                    .disabled(coordinator.state.t3ConnectionStatus == .openingSignIn)

                    Text("In T3 Code, open Settings → Environments and choose Copy MCP URL.")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                }

                VStack(alignment: .leading, spacing: 6) {
                    LabeledContent("Status", value: coordinator.state.t3ConnectionStatus.rawValue)

                    if let detail = coordinator.state.t3StatusDetail {
                        Text(UserFacingStatus.t3Detail(status: coordinator.state.t3ConnectionStatus, message: detail))
                            .font(.caption)
                            .foregroundStyle(.secondary)
                            .fixedSize(horizontal: false, vertical: true)
                    }
                }

                HStack {
                    switch coordinator.state.t3ConnectionStatus {
                    case .openingSignIn:
                        Button("Cancel") {
                            coordinator.cancelT3SignIn()
                        }
                        .accessibilityLabel("Cancel T3 sign-in")
                    case .connected:
                        Button("Disconnect") {
                            coordinator.disconnectT3()
                        }
                        .accessibilityLabel("Disconnect T3 Code")
                    case .notConnected, .signInAgain, .couldNotReach:
                        Button("Connect T3 Code") {
                            coordinator.connectT3()
                        }
                        .disabled(coordinator.settings.t3Address.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
                    }
                }

                if coordinator.state.t3ConnectionStatus == .connected {
                    Text("Your sign-in is saved in Keychain.")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                } else if coordinator.state.t3ConnectionStatus != .openingSignIn {
                    Text("Opens your browser so you can approve read-only access in T3 Code. Developer Presence only reads which agent tools are working and how many tasks and helpers they have.")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                }
            }

            // 4. Discord Section
            Section("Discord") {
                VStack(alignment: .leading, spacing: 6) {
                    TextField("Discord application ID", text: Binding(
                        get: { coordinator.settings.discordClientId },
                        set: { coordinator.setDiscordClientId($0) }
                    ))
                    .textFieldStyle(.roundedBorder)

                    if !coordinator.settings.discordClientId.isEmpty && !coordinator.settings.isValidDiscordClientId {
                        Label("Application IDs are 15 to 20 digits. Check that you copied the whole number.", systemImage: "exclamationmark.circle")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                            .fixedSize(horizontal: false, vertical: true)
                    }

                    Text("Find it under General Information for your app in the Discord Developer Portal. It’s public, not a password or token.")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                }

                Link("Open Discord Developer Portal", destination: URL(string: "https://discord.com/developers/applications")!)

                VStack(alignment: .leading, spacing: 6) {
                    Toggle("Show app icons", isOn: Binding(
                        get: { coordinator.settings.showAppIcons },
                        set: { value in
                            if value != coordinator.settings.showAppIcons { coordinator.toggleShowAppIcons() }
                        }
                    ))

                    Text("Adds the T3 Code icon and agent tool icons to your status. Add the icons to your Discord application first, or they won’t appear.")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                }

                Link("How to Add the Icons", destination: URL(string: "https://github.com/geekynasir/developer-presence/blob/main/docs/connect-t3.md#add-logos")!)
            }

            // 5. Privacy Footer
            Section {
                Text(privacyFooterText)
                    .font(.footnote)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
        .formStyle(.grouped)
    }

    // MARK: - App Detection Tab

    private var appDetectionTab: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("If T3 Code, Claude, or Codex isn’t recognized, add its version here.")
                .font(.body)

            if let bundleId = coordinator.state.detectedBundleId {
                Text("Current app: \(bundleId)")
                    .font(.caption)
                    .textSelection(.enabled)
                    .foregroundStyle(.secondary)
            }

            List {
                ForEach(coordinator.settings.bundleMappings.keys.sorted(), id: \.self) { id in
                    HStack {
                        VStack(alignment: .leading, spacing: 2) {
                            Text(coordinator.settings.bundleMappings[id]?.displayName ?? "Unknown app")
                                .font(.body)
                            Text(id)
                                .font(.caption)
                                .foregroundStyle(.secondary)
                        }
                        Spacer()
                        Button(role: .destructive) {
                            coordinator.removeBundleMapping(bundleId: id)
                        } label: {
                            Image(systemName: "trash")
                        }
                        .buttonStyle(.borderless)
                        .accessibilityLabel("Remove \(id)")
                    }
                }
            }

            VStack(alignment: .leading, spacing: 4) {
                Text("App identifier")
                    .font(.caption)
                    .foregroundStyle(.secondary)

                TextField("App identifier", text: $newBundleId, prompt: Text("com.company.app"))
                    .textFieldStyle(.roundedBorder)
                    .accessibilityLabel("App identifier")

                Text("Also called a bundle ID, for example com.company.app.")
                    .font(.caption2)
                    .foregroundStyle(.secondary)
            }

            HStack {
                Picker("Recognize as", selection: $selectedApp) {
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

            Button("Restore Defaults") {
                coordinator.resetBundleMappingsToDefaults()
            }
        }
        .padding(20)
    }

    // MARK: - Helpers

    private var previewHeadline: String {
        coordinator.state.outgoingHeadline == ActivityFormatter.headlineNone ? "Not shown on Discord" : coordinator.state.outgoingHeadline
    }

    private var sharingStatus: UserFacingStatus.Message {
        UserFacingStatus.describe(state: coordinator.state, settings: coordinator.settings)
    }

    private var previewAccessibilityLabel: String {
        if coordinator.state.outgoingHeadline == ActivityFormatter.headlineNone { return "Not shown on Discord" }
        let details = coordinator.state.outgoingHeadline.replacingOccurrences(of: " · ", with: ", ")
        if let state = coordinator.state.outgoingState {
            let stateClean = state.replacingOccurrences(of: " · ", with: ", ")
            return "Discord will show: \(details). \(stateClean)."
        }
        return "Discord will show: \(details)."
    }

    private var privacyFooterText: String {
        if coordinator.settings.showT3Activity {
            return "Discord sees the app name and, while T3 Code is active, an agent tool name with task and helper counts. Conversations, thread titles, projects, models, and files are never shared."
        } else {
            return "Discord sees only the name of the app you’re using. Conversations, window titles, files, and projects are never shared."
        }
    }
}

import SwiftUI
import AppKit

@MainActor
public struct SettingsView: View {
    @ObservedObject public var coordinator: PresenceCoordinator
    @State private var newBundleId = ""
    @State private var selectedApp: SupportedApp = .t3Code
    @State private var selectedTab = CommandLine.arguments.contains("--show-computers") ? 1 : 0

    public init(coordinator: PresenceCoordinator) {
        self.coordinator = coordinator
    }

    public var body: some View {
        TabView(selection: $selectedTab) {
            generalTab
                .tabItem { Label("General", systemImage: "gearshape") }
                .tag(0)

            PresenceControlsView(coordinator: coordinator)
                .tabItem { Label("Card", systemImage: "rectangle.on.rectangle") }
                .tag(3)

            Form { ComputerSettingsView(coordinator: coordinator, computers: coordinator.computers) }
                .formStyle(.grouped)
                .tabItem { Label("Computers", systemImage: "desktopcomputer") }
                .tag(1)

            appDetectionTab
                .tabItem { Label("App Detection", systemImage: "app.badge") }
                .tag(2)
        }
        .frame(minWidth: 540, minHeight: 560)
    }

    // MARK: - General Tab

    private var generalTab: some View {
        Form {
            Section {
                Text("Open Discord and your developer app, then turn on sharing. Deskchirp connects to Discord automatically.")
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }

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
                HStack(alignment: .top, spacing: 14) {
                    if let image = previewImage {
                        Image(nsImage: image).resizable().frame(width: 52, height: 52)
                            .accessibilityHidden(true)
                    }
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

                        if let note = coordinator.state.t3ActivityNote {
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
                }
                .accessibilityElement(children: .combine)
                .accessibilityLabel(previewAccessibilityLabel)
            }

            // 3. T3 Code section
            Section("T3 Code") {
                VStack(alignment: .leading, spacing: 6) {
                    if coordinator.state.t3ConnectionStatus == .connected {
                        Toggle("Show T3 activity", isOn: Binding(
                            get: { coordinator.settings.showT3Activity },
                            set: { value in
                                if value != coordinator.settings.showT3Activity { coordinator.toggleT3Activity() }
                            }
                        ))
                    }

                    Text("Show the busiest agent tool and its task and helper counts. A helper is an extra agent working on part of a task. Each T3 thread counts as a task.")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                }

                VStack(alignment: .leading, spacing: 6) {
                    LabeledContent("Status", value: coordinator.isFindingT3 ? "Finding T3 Code…" : coordinator.state.t3ConnectionStatus.rawValue)

                    if let message = coordinator.t3SetupMessage {
                        Text(message).font(.caption).foregroundStyle(.secondary)
                            .fixedSize(horizontal: false, vertical: true)
                    }

                    if let detail = coordinator.state.t3StatusDetail {
                        Text(UserFacingStatus.t3Detail(status: coordinator.state.t3ConnectionStatus, message: detail))
                            .font(.caption)
                            .foregroundStyle(.secondary)
                            .fixedSize(horizontal: false, vertical: true)
                    }
                }

                HStack {
                    if coordinator.isFindingT3 {
                        Button("Cancel") { coordinator.cancelT3SignIn() }
                            .accessibilityLabel("Cancel finding T3 Code")
                    } else {
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
                        Button("Allow T3 activity") {
                            coordinator.connectT3()
                        }

                        }
                    }
                }

                if coordinator.state.t3ConnectionStatus == .connected {
                    Text("Your sign-in is saved in Keychain.")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                } else if !coordinator.isFindingT3 && coordinator.state.t3ConnectionStatus != .openingSignIn {
                    Text("Opens your browser so you can approve read-only access in T3 Code. Deskchirp only reads which agent tools are working and how many tasks and helpers they have.")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                }
            }

            EnvironmentsView(coordinator: coordinator)

            // 4. Discord Section
            DisclosureGroup("Advanced") {
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

                    Text("Deskchirp’s application ID is included. Change this only to use your own Discord application.")
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

                    Text("Included app and agent artwork. No upload is needed.")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                }

                Link("How to Add the Icons", destination: URL(string: "https://github.com/geekynasir/deskchirp-discord/blob/main/docs/connect-t3.md#add-logos")!)
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

    private var previewImage: NSImage? {
        let headline = coordinator.state.outgoingHeadline
        let key = headline.contains("T3 Code") ? "t3_code" : headline == "Using Claude" ? "harness_claude" : headline == "Using Codex" ? "harness_codex" : "deskchirp"
        guard let url = Bundle.main.url(forResource: key, withExtension: "png", subdirectory: "Discord-artwork") else { return nil }
        return NSImage(contentsOf: url)
    }

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
        let settings = coordinator.settings
        guard settings.showT3Activity, settings.cardStyle != .appOnly else {
            return "Discord sees only the name of the app you’re using. Conversations, window titles, files, and projects are never shared."
        }
        var shared = ["the app name", "T3 agent tool names with task and helper counts, including work running in the background"]
        if settings.shareProjectAlias { shared.append("the public project names you write") }
        if settings.shareModel { shared.append("the model name") }
        if settings.cardStyle == .usage { shared.append("the token number you chose") }
        return "Discord sees " + shared.joined(separator: ", ") + ". Conversations, thread titles, real project names, environment names, and files are never shared."
    }
}

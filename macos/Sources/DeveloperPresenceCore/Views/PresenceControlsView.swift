import SwiftUI
import AppKit

/// Card tab: style, usage options, badge, sharing toggles, projects, buttons, selection, and live preview.
@MainActor
public struct PresenceControlsView: View {
    @ObservedObject public var coordinator: PresenceCoordinator
    @State private var aliasProject = ""
    @State private var aliasText = ""

    public init(coordinator: PresenceCoordinator) {
        self.coordinator = coordinator
    }

    private var settings: PresenceSettings { coordinator.settings }

    public var body: some View {
        Form {
            previewSection
            styleSection
            if settings.cardStyle == .usage { usageSection }
            detailsSection
            projectsSection
            buttonsSection
            selectionSection
        }
        .formStyle(.grouped)
    }

    // MARK: - Preview

    private var previewSection: some View {
        Section("What Discord receives") {
            let card = coordinator.previewCard
            if card.activity == nil || coordinator.state.outgoingHeadline == ActivityFormatter.headlineNone {
                Text("Nothing is sent to Discord right now.")
                    .foregroundStyle(.secondary)
            } else {
                ForEach(card.previewRows) { row in
                    VStack(alignment: .leading, spacing: 2) {
                        LabeledContent(row.label) {
                            Text(displayValue(row))
                                .multilineTextAlignment(.trailing)
                                .textSelection(.enabled)
                        }
                        Text("From \(row.source.rawValue)")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                        if card.fitWarnings.contains(row.field) {
                            Label(PresenceCardFormatter.fitWarningText, systemImage: "exclamationmark.triangle")
                                .font(.caption)
                                .foregroundStyle(.secondary)
                                .fixedSize(horizontal: false, vertical: true)
                        }
                    }
                    .accessibilityElement(children: .combine)
                    .accessibilityLabel(rowAccessibilityLabel(row, warned: card.fitWarnings.contains(row.field)))
                }
            }
            Text("Every line above is exactly what Deskchirp sends. Titles, prompts, paths, project names, environment names and addresses are never sent.")
                .font(.caption)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
        }
    }

    private func displayValue(_ row: PresencePreviewRow) -> String {
        if row.field == "timestampsStart", let seconds = TimeInterval(row.value) {
            let date = Date(timeIntervalSince1970: seconds)
            return "Since \(date.formatted(date: .omitted, time: .shortened))"
        }
        return row.value
    }

    private func rowAccessibilityLabel(_ row: PresencePreviewRow, warned: Bool) -> String {
        var text = "\(row.label): \(displayValue(row)), from \(row.source.rawValue)."
        if warned { text += " \(PresenceCardFormatter.fitWarningText)" }
        return text
    }

    // MARK: - Style

    private var styleSection: some View {
        Section("Card") {
            Picker("Card style", selection: Binding(
                get: { settings.cardStyle },
                set: { value in
                    guard reason(for: value) == nil else { return }
                    coordinator.updateControls { $0.cardStyle = value }
                }
            )) {
                Text("App only").tag(PresenceCardStyle.appOnly)
                Text("Work in progress").tag(PresenceCardStyle.workload)
                Text("Token usage").tag(PresenceCardStyle.usage)
            }
            .pickerStyle(.radioGroup)
            .accessibilityLabel("Card style")

            ForEach(PresenceCardStyle.allCases, id: \.self) { style in
                if let reason = reason(for: style) {
                    Text("\(styleName(style)): \(reason)")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                }
            }

            Picker("Small badge", selection: Binding(
                get: { settings.smallBadge },
                set: { value in coordinator.updateControls { $0.smallBadge = value } }
            )) {
                Text("Work status").tag(PresenceSmallBadge.status)
                Text("Agent tool").tag(PresenceSmallBadge.provider)
                Text("None").tag(PresenceSmallBadge.hidden)
            }
            Text("The small picture on the corner of the app image. Needs Show app icons.")
                .font(.caption)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
        }
    }

    private func styleName(_ style: PresenceCardStyle) -> String {
        switch style {
        case .appOnly: return "App only"
        case .workload: return "Work in progress"
        case .usage: return "Token usage"
        }
    }

    private func reason(for style: PresenceCardStyle) -> String? {
        PresenceAvailability.cardStyleReason(
            style, settings: settings,
            anyEnvironmentConnected: coordinator.anyEnvironmentConnected,
            anyUsageAllowed: coordinator.anyUsageAllowed)
    }

    // MARK: - Usage

    private var usageSection: some View {
        Section("Token usage") {
            Picker("Show", selection: Binding(
                get: { settings.usageMetric },
                set: { value in coordinator.updateControls { $0.usageMetric = value } }
            )) {
                Text("Tokens").tag(PresenceUsageMetric.tokens)
                Text("Context used").tag(PresenceUsageMetric.context)
            }

            Picker("Count", selection: Binding(
                get: { settings.usageScope },
                set: { value in
                    guard PresenceAvailability.scopeReason(value, settings: settings) == nil else { return }
                    coordinator.updateControls { $0.usageScope = value }
                }
            )) {
                Text("Today").tag(PresenceUsageScope.today)
                Text("This work session").tag(PresenceUsageScope.session)
                Text("Pinned task").tag(PresenceUsageScope.pinnedTask)
            }
            .disabled(settings.usageMetric == .context)
            if settings.usageMetric == .context {
                Text(PresenceAvailability.contextScopeReason)
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
                if let reason = PresenceAvailability.scopeReason(.pinnedTask, settings: settings) {
                    Text(reason)
                        .font(.caption)
                        .foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                }
            } else {
                ForEach(PresenceUsageScope.allCases, id: \.self) { scope in
                    if let reason = PresenceAvailability.scopeReason(scope, settings: settings) {
                        Text("\(scopeName(scope)): \(reason)")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                            .fixedSize(horizontal: false, vertical: true)
                    }
                }
            }

            if settings.usageMetric == .tokens {
                Picker("Tokens", selection: Binding(
                    get: { settings.tokenCategory },
                    set: { value in coordinator.updateControls { $0.tokenCategory = value } }
                )) {
                    Text("All").tag(PresenceTokenCategory.total)
                    Text("Input only").tag(PresenceTokenCategory.input)
                    Text("Output only").tag(PresenceTokenCategory.output)
                }
                Picker("Numbers", selection: Binding(
                    get: { settings.tokenRounding },
                    set: { value in coordinator.updateControls { $0.tokenRounding = value } }
                )) {
                    Text("Short, like 842K").tag(PresenceTokenRounding.compact)
                    Text("Exact, like 842,112").tag(PresenceTokenRounding.exact)
                }
            }

            if PresenceAvailability.showsCostOption(settings) {
                Toggle("Share estimated cost", isOn: Binding(
                    get: { settings.shareEstimatedCost },
                    set: { value in coordinator.updateControls { $0.shareEstimatedCost = value } }
                ))
                Text("An estimate at API prices, shown as “API est.”. It isn’t your bill.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }

            Text(PresenceCardFormatter.usageDefinition(metric: settings.usageMetric, scope: PresenceCardFormatter.effectiveScope(settings)) + ".")
                .font(.caption)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
            Text("If the number isn’t complete or up to date, the card shows work in progress instead.")
                .font(.caption)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
        }
    }

    private func scopeName(_ scope: PresenceUsageScope) -> String {
        switch scope {
        case .today: return "Today"
        case .session: return "This work session"
        case .pinnedTask: return "Pinned task"
        }
    }

    // MARK: - Details

    private var detailsSection: some View {
        Section("Details") {
            Toggle("Show elapsed time", isOn: Binding(
                get: { settings.showElapsedTimer },
                set: { value in coordinator.updateControls { $0.showElapsedTimer = value } }
            ))
            Toggle("Share model name", isOn: Binding(
                get: { settings.shareModel },
                set: { value in coordinator.updateControls { $0.shareModel = value } }
            ))
            Toggle("Playful wording", isOn: Binding(
                get: { settings.playfulWording },
                set: { value in coordinator.updateControls { $0.playfulWording = value } }
            ))
            Text("Playful wording says things like “Herding 4 agents”.")
                .font(.caption)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
        }
    }

    // MARK: - Projects

    private var projectsSection: some View {
        Section("Projects") {
            Toggle("Share project names you write", isOn: Binding(
                get: { settings.shareProjectAlias },
                set: { value in coordinator.updateControls { $0.shareProjectAlias = value } }
            ))
            Text("Discord never sees real project names. Write a short public name for a project, and Deskchirp shows it instead of the agent tool.")
                .font(.caption)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)

            let choices = coordinator.projectChoices
            if choices.isEmpty {
                Text("Projects appear here after T3 Code shares activity.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            } else {
                HStack {
                    Picker("Project", selection: $aliasProject) {
                        Text("Choose a project").tag("")
                        ForEach(choices) { choice in
                            Text("\(choice.title) (\(choice.environmentLabel))").tag(choice.id)
                        }
                    }
                    .accessibilityLabel("Project to name")
                }
                HStack {
                    TextField("Public name", text: $aliasText, prompt: Text("Up to 32 characters"))
                        .textFieldStyle(.roundedBorder)
                        .accessibilityLabel("Public name for the project")
                    Button("Save Name") {
                        let key = aliasProject
                        let value = aliasText
                        coordinator.updateControls { $0.projectAliases[key] = value }
                        aliasText = ""
                    }
                    .disabled(aliasProject.isEmpty || PresenceCardFormatter.cleanAlias(aliasText) == nil)
                }

                ForEach(settings.projectAliases.keys.sorted(), id: \.self) { key in
                    HStack {
                        VStack(alignment: .leading, spacing: 2) {
                            Text(settings.projectAliases[key] ?? "")
                            Text(projectTitle(key, choices: choices))
                                .font(.caption)
                                .foregroundStyle(.secondary)
                        }
                        Spacer()
                        Button(role: .destructive) {
                            coordinator.updateControls { $0.projectAliases.removeValue(forKey: key) }
                        } label: {
                            Image(systemName: "trash")
                        }
                        .buttonStyle(.borderless)
                        .accessibilityLabel("Remove public name \(settings.projectAliases[key] ?? "")")
                    }
                }

                DisclosureGroup("Hide projects") {
                    ForEach(choices) { choice in
                        Toggle("\(choice.title) (\(choice.environmentLabel))", isOn: Binding(
                            get: { settings.excludedProjects.contains(choice.id) },
                            set: { hidden in
                                coordinator.updateControls {
                                    if hidden { $0.excludedProjects.insert(choice.id) } else { $0.excludedProjects.remove(choice.id) }
                                }
                            }
                        ))
                    }
                    Text("Hidden projects and their helpers are left out of every count.")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                }

                Picker("Pinned project", selection: Binding(
                    get: { settings.pinnedProject },
                    set: { value in coordinator.updateControls { $0.pinnedProject = value } }
                )) {
                    Text("None").tag("")
                    if !settings.pinnedProject.isEmpty && !choices.contains(where: { $0.id == settings.pinnedProject }) {
                        Text("Pinned project (not seen recently)").tag(settings.pinnedProject)
                    }
                    ForEach(choices) { choice in
                        Text("\(choice.title) (\(choice.environmentLabel))").tag(choice.id)
                    }
                }
            }
            if coordinator.pinnedProjectNotFound {
                Label(PresenceAvailability.pinnedProjectNotFoundText, systemImage: "exclamationmark.circle")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
            if !settings.pinnedProject.isEmpty {
                HStack {
                    Text("Only work in the pinned project is counted.")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                    Spacer()
                    Button("Unpin Project") { coordinator.updateControls { $0.pinnedProject = "" } }
                }
            }

            let tasks = coordinator.taskChoices
            Picker("Pinned task", selection: Binding(
                get: { settings.pinnedTask },
                set: { value in coordinator.updateControls { $0.pinnedTask = value } }
            )) {
                Text("None").tag("")
                if !settings.pinnedTask.isEmpty && !tasks.contains(where: { $0.id == settings.pinnedTask }) {
                    Text("Pinned task (not running now)").tag(settings.pinnedTask)
                }
                ForEach(tasks) { task in
                    Text("\(task.title) (\(task.environmentLabel))").tag(task.id)
                }
            }
            Text("A pinned task’s tokens or context can be shown. Task titles stay on this Mac.")
                .font(.caption)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
        }
    }

    private func projectTitle(_ key: String, choices: [PresenceCoordinator.ProjectChoice]) -> String {
        if let choice = choices.first(where: { $0.id == key }) {
            return "\(choice.title) (\(choice.environmentLabel))"
        }
        return "Project not seen recently"
    }

    // MARK: - Buttons

    private var buttonsSection: some View {
        Section("Buttons") {
            Picker("Buttons", selection: Binding(
                get: { settings.buttons },
                set: { value in coordinator.updateControls { $0.buttons = value } }
            )) {
                Text("None").tag(PresenceButtons.hidden)
                Text("Get Deskchirp").tag(PresenceButtons.companion)
                Text("Get Deskchirp and View project").tag(PresenceButtons.companionAndRepo)
            }
            if settings.buttons == .companionAndRepo {
                TextField("Public repository link", text: Binding(
                    get: { settings.publicRepoUrl },
                    set: { value in
                        coordinator.updateControls { $0.publicRepoUrl = value.trimmingCharacters(in: .whitespacesAndNewlines) }
                    }
                ), prompt: Text("https://github.com/owner/repository"))
                .textFieldStyle(.roundedBorder)
                .accessibilityLabel("Public repository link")
                if let reason = PresenceAvailability.repoButtonReason(settings) {
                    Label(reason, systemImage: "exclamationmark.circle")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                }
                Text("View project appears only while a public project name is shown.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
    }

    // MARK: - Selection

    private var selectionSection: some View {
        Section("When another app is in front") {
            Picker("Show", selection: Binding(
                get: { settings.selectionMode },
                set: { value in coordinator.updateControls { $0.selectionMode = value } }
            )) {
                Text("Keep T3 work visible").tag(PresenceSelectionMode.keepBackgroundVisible)
                Text("The app in front").tag(PresenceSelectionMode.followActiveApp)
            }
            .pickerStyle(.radioGroup)
            .accessibilityLabel("When another app is in front")
            Text(settings.selectionMode == .keepBackgroundVisible
                 ? "Running T3 work stays on your card even while you use Claude or Codex."
                 : "Your card follows the app you’re using. T3 work shows only while T3 Code is in front or is the only app open.")
                .font(.caption)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
        }
    }
}

/// T3 environments list with per-environment sign-in and token usage pairing.
@MainActor
public struct EnvironmentsView: View {
    @ObservedObject public var coordinator: PresenceCoordinator
    @State private var newAddress = ""
    @State private var pairingInputs: [String: String] = [:]

    public init(coordinator: PresenceCoordinator) {
        self.coordinator = coordinator
    }

    public var body: some View {
        Section("T3 environments") {
            Text("Add every T3 environment where your tasks run, including remote ones.")
                .font(.caption)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)

            ForEach(coordinator.settings.t3Environments) { environment in
                environmentRow(environment)
            }

            VStack(alignment: .leading, spacing: 6) {
                HStack {
                    TextField("Environment address", text: $newAddress, prompt: Text("Paste from Copy MCP URL"))
                        .textFieldStyle(.roundedBorder)
                        .accessibilityLabel("New environment address")
                    Button("Add Environment") {
                        if coordinator.addEnvironment(address: newAddress) { newAddress = "" }
                    }
                    .disabled(newAddress.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
                }
                Text("In T3 Code on that computer, open Settings → Environments and choose Copy MCP URL.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
                if let message = coordinator.environmentMessage {
                    Text(message)
                        .font(.caption)
                        .foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                }
            }
        }
    }

    @ViewBuilder
    private func environmentRow(_ environment: T3Environment) -> some View {
        let status = coordinator.environmentStatus(environment.id)
        let usage = coordinator.usageStatus(environment.id)
        VStack(alignment: .leading, spacing: 6) {
            TextField("Name on this Mac", text: Binding(
                get: { environment.label },
                set: { coordinator.renameEnvironment(id: environment.id, label: $0) }
            ))
            .textFieldStyle(.roundedBorder)
            .accessibilityLabel("Name for \(environment.label) on this Mac")
            Text(environment.address)
                .font(.caption)
                .foregroundStyle(.secondary)
                .textSelection(.enabled)
            LabeledContent("Status", value: status.rawValue)
            Text(coordinator.environmentDetail(environment.id))
                .font(.caption)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)

            HStack {
                switch status {
                case .openingSignIn:
                    Button("Cancel") { coordinator.cancelT3SignIn() }
                        .accessibilityLabel("Cancel sign-in for \(environment.label)")
                case .connected:
                    Button("Disconnect") { coordinator.disconnectEnvironment(id: environment.id) }
                        .accessibilityLabel("Disconnect \(environment.label)")
                case .notConnected, .signInAgain, .couldNotReach:
                    Button("Allow") { coordinator.connectEnvironment(id: environment.id) }
                        .accessibilityLabel("Allow T3 activity for \(environment.label)")
                }
                Spacer()
                Button("Remove", role: .destructive) { coordinator.removeEnvironment(id: environment.id) }
                    .accessibilityLabel("Remove \(environment.label)")
            }

            DisclosureGroup("Token usage: \(usage.title)") {
                usageControls(environment, usage: usage)
            }
        }
        .padding(.vertical, 4)
    }

    @ViewBuilder
    private func usageControls(_ environment: T3Environment, usage: T3UsagePermissionStatus) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            Text(T3UsagePairing.permissionWording)
                .font(.caption)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
            if usage.isAllowed {
                Button("Remove Token Access", role: .destructive) {
                    coordinator.removeUsageAccess(environmentId: environment.id)
                }
                .accessibilityLabel("Remove token access for \(environment.label)")
            } else {
                Text(T3UsagePairing.explanation)
                    .font(.caption)
                    .fixedSize(horizontal: false, vertical: true)
                Text(T3UsagePairing.command)
                    .font(.system(.caption, design: .monospaced))
                    .textSelection(.enabled)
                    .fixedSize(horizontal: false, vertical: true)
                    .accessibilityLabel("Command to run: \(T3UsagePairing.command)")
                Button("Copy Command") {
                    NSPasteboard.general.clearContents()
                    NSPasteboard.general.setString(T3UsagePairing.command, forType: .string)
                }
                HStack {
                    SecureField("Pairing code", text: Binding(
                        get: { pairingInputs[environment.id] ?? "" },
                        set: { pairingInputs[environment.id] = $0 }
                    ), prompt: Text("Code or pairing link"))
                    .textFieldStyle(.roundedBorder)
                    .accessibilityLabel("Pairing code for \(environment.label)")
                    Button("Allow Token Usage") {
                        coordinator.pairUsage(environmentId: environment.id, input: pairingInputs[environment.id] ?? "")
                        pairingInputs[environment.id] = ""
                    }
                    .disabled((pairingInputs[environment.id] ?? "").trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
                              || usage == .pairing)
                    .accessibilityLabel("Allow token usage for \(environment.label)")
                }
            }
        }
    }
}

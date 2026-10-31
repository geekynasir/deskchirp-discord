import SwiftUI

@MainActor
struct ComputerSettingsView: View {
    @ObservedObject var coordinator: PresenceCoordinator
    @ObservedObject var computers: ComputerLinkService
    @State private var address = ""
    @State private var name = String((Host.current().localizedName ?? "My Mac").prefix(48))
    @State private var code = ""
    @State private var expanded = true

    var body: some View {
        Section("Computers") {
            DisclosureGroup("Follow me", isExpanded: $expanded) {
                VStack(alignment: .leading, spacing: 12) {
                    Text("Share from the computer you most recently used. Other linked computers wait. Switching waits briefly for the previous computer to stop. Activity hides after five minutes without input.")
                    Text(computers.status)
                        .accessibilityAddTraits(.updatesFrequently)
                    Link("How to Link Your Computers", destination: URL(string: "https://github.com/geekynasir/deskchirp-discord/blob/main/docs/computers.md")!)
                    if !computers.hasSavedLink {
                        TextField("Linking address", text: $address, prompt: Text("Paste your linking address"))
                        TextField("Computer name", text: $name)
                            .onChange(of: name) { value in name = String(value.prefix(48)) }
                        SecureField("Linking code", text: $code)
                        Text("Use a new code from a linked computer. For your first computer, get a code from your linking service. Use the same Discord account on every computer.")
                            .font(.caption).foregroundStyle(.secondary)
                        Button("Link This Computer") {
                            coordinator.linkComputer(address: address, code: code, name: name)
                            code = ""
                        }
                        .disabled(address.isEmpty || name.isEmpty || code.isEmpty)
                    } else {
                        Text(coordinator.settings.computerLinkAddress).textSelection(.enabled)
                        Button("Link Another Computer") { Task { await computers.createCode() } }
                    }
                    if !computers.pairingCode.isEmpty {
                        Text(computers.pairingCode).textSelection(.enabled)
                        Text("Use once within five minutes. Keep this code private.")
                            .font(.caption).foregroundStyle(.secondary)
                    }
                    if coordinator.settings.followComputers {
                        Button("Unlink This Computer") { coordinator.unlinkComputer() }
                    }
                    Text("Your linking service sees computer names and recent-use timing. Your app activity stays on your computers. Pause sharing affects this computer only. Unlinking turns sharing off here.")
                        .font(.caption).foregroundStyle(.secondary)
                }
                .fixedSize(horizontal: false, vertical: true)
                .disabled(computers.isBusy)
            }
        }
        .onAppear { address = coordinator.settings.computerLinkAddress }
    }
}

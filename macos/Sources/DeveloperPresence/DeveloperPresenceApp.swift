import SwiftUI
import AppKit
import DeveloperPresenceCore

@MainActor
private enum AppModel {
    static let coordinator = PresenceCoordinator()
}

@MainActor
private final class AppDelegate: NSObject, NSApplicationDelegate {
    func applicationDidFinishLaunching(_ notification: Notification) {
        NSApp.setActivationPolicy(.accessory)
        AppModel.coordinator.start()

        // Support --show-settings launch argument for CI native screenshots
        if CommandLine.arguments.contains("--show-settings") {
            SettingsWindowController.shared.showSettings(coordinator: AppModel.coordinator)
        }
    }

    func applicationWillTerminate(_ notification: Notification) {
        AppModel.coordinator.stop()
    }
}

@main
@MainActor
struct DeveloperPresenceApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var delegate
    @StateObject private var coordinator = AppModel.coordinator

    var body: some Scene {
        MenuBarExtra("Developer Presence", systemImage: coordinator.menuBarIconName) {
            MenuBarContentView(coordinator: coordinator)
        }
    }
}

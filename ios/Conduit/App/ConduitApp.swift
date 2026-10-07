import SwiftUI

@main
struct ConduitApp: App {
    @State private var model = AppModel()
    @Environment(\.scenePhase) private var scenePhase

    var body: some Scene {
        WindowGroup {
            RootView()
                .environment(model)
                .tint(Brand.clay)
                .onOpenURL { model.handle(url: $0) }
        }
        .onChange(of: scenePhase) { _, phase in
            model.scenePhaseChanged(phase)
        }
    }
}

struct RootView: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        Group {
            if model.hosts.isEmpty {
                WelcomeView()
                    .transition(.opacity.combined(with: .scale(scale: 0.98)))
            } else {
                MainTabView()
                    .transition(.opacity)
            }
        }
        .animation(.smooth(duration: 0.45), value: model.hosts.isEmpty)
    }
}

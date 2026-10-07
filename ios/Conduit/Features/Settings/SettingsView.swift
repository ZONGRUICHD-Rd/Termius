import ConduitKit
import SwiftUI

struct SettingsView: View {
    @Environment(AppModel.self) private var model
    @AppStorage(LiveActivityController.enabledKey) private var liveActivities = true
    @AppStorage("showReasoning") private var showReasoning = false
    @AppStorage("apnsEnvironment") private var apnsEnvironment = "production"

    var body: some View {
        NavigationStack {
            List {
                Section {
                    Toggle(isOn: $liveActivities) {
                        Label("Dynamic Island & Lock Screen", systemImage: "platter.filled.top.iphone")
                    }
                    .disabled(!RuntimeEnvironment.liveActivitiesSupported)
                    .onChange(of: liveActivities) { _, on in
                        if !on { model.liveActivity.endNow() }
                    }
                    if !RuntimeEnvironment.liveActivitiesSupported {
                        Text(liveActivityUnavailableReason).font(.footnote).foregroundStyle(.secondary)
                    }
                    Picker("APNs environment", selection: $apnsEnvironment) {
                        Text("Production").tag("production")
                        Text("Development").tag("sandbox")
                    }
                } header: {
                    EyebrowLabel(text: "Live Activity")
                } footer: {
                    Text("While Conduit is open it updates the Island itself. To keep it live in the background, add an APNs key in Conduit Host → Settings.")
                }

                Section {
                    Toggle(isOn: $showReasoning) { Label("Show agent thinking", systemImage: "brain") }
                } header: { EyebrowLabel(text: "Chat") }

                Section {
                    LabeledContent("Version", value: RuntimeEnvironment.appVersion)
                    LabeledContent("Runtime", value: RuntimeEnvironment.isLiveContainer ? "LiveContainer" : "Native")
                    LabeledContent("Device key") {
                        Text(model.identity.deviceId).font(.caption.monospaced()).foregroundStyle(.secondary)
                    }
                    LabeledContent("Encryption", value: "P-256 ECDH · AES-256-GCM")
                } header: { EyebrowLabel(text: "About") } footer: {
                    Text("All traffic is end-to-end encrypted between this iPhone and your computer. Relays only see ciphertext.")
                }
            }
            .scrollContentBackground(.hidden)
            .paperBackground()
            .navigationTitle("Settings")
        }
    }

    private var liveActivityUnavailableReason: String {
        if RuntimeEnvironment.isLiveContainer {
            return "LiveContainer can't host app extensions, so Conduit shows its in-app Island instead."
        }
        if !RuntimeEnvironment.hasWidgetExtension {
            return "This build doesn't include the widget extension. The in-app Island is used instead."
        }
        return "Live Activities are turned off for Conduit in iOS Settings."
    }
}

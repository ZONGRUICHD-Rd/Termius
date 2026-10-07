import ConduitKit
import SwiftUI

struct HostsView: View {
    @Environment(AppModel.self) private var model
    @State private var confirmRemove: PairedHost?

    var body: some View {
        NavigationStack {
            List {
                ForEach(model.hosts) { host in
                    let isActive = host.id == model.activeHostId
                    Section {
                        Button {
                            if !isActive { model.connect(to: host.id) }
                        } label: {
                            HStack(spacing: 14) {
                                Image(systemName: "desktopcomputer")
                                    .font(.title2)
                                    .foregroundStyle(isActive ? Brand.clay : Brand.inkSoft)
                                    .frame(width: 44, height: 44)
                                    .glassEffect(isActive ? .regular.tint(Brand.clay.opacity(0.2)) : .regular, in: .circle)
                                VStack(alignment: .leading, spacing: 3) {
                                    Text(host.name).font(.serif(.headline, weight: .semibold)).foregroundStyle(Brand.ink)
                                    if isActive, let client = model.client {
                                        ConnectionBadge(status: client.status, rtt: client.rttMs)
                                    } else {
                                        Text("Tap to switch").font(.caption).foregroundStyle(.secondary)
                                    }
                                }
                                Spacer()
                                if isActive { Image(systemName: "checkmark.circle.fill").foregroundStyle(Brand.clay) }
                            }
                        }
                        .buttonStyle(.plain)

                        if isActive, let client = model.client {
                            if let info = client.hostInfo {
                                LabeledContent("Conduit Host", value: info.version)
                                LabeledContent("System", value: info.os)
                                ForEach(info.agents) { a in
                                    LabeledContent {
                                        Text(a.available ? (a.version ?? "Installed") : "Not installed")
                                            .foregroundStyle(a.available ? Color.secondary : Brand.rust)
                                    } label: {
                                        Label { Text(a.name) } icon: { AgentGlyph(agent: a.id, size: 16) }
                                    }
                                }
                            }
                            DisclosureGroup("Routes") {
                                ForEach(Endpoint.candidates(for: client.host), id: \.self) { e in
                                    HStack {
                                        Text(e.kind.label).font(.caption.weight(.semibold))
                                        Spacer()
                                        Text(e.url.host ?? e.url.absoluteString).font(.caption.monospaced()).foregroundStyle(.secondary)
                                    }
                                }
                                if client.host.relay == nil {
                                    Text("Tip: set a relay URL or install Tailscale on both devices to connect from anywhere.")
                                        .font(.caption).foregroundStyle(.secondary)
                                }
                            }
                            if case .failed = client.status {
                                Button("Retry now") { client.resume() }
                            }
                        }
                        Button("Forget this computer", role: .destructive) { confirmRemove = host }
                    }
                    .listRowBackground(Brand.paperDeep.opacity(0.6))
                }
            }
            .scrollContentBackground(.hidden)
            .paperBackground()
            .navigationTitle("Computers")
            .toolbar {
                ToolbarItem(placement: .topBarTrailing) {
                    Button {
                        model.showPairing = true
                    } label: {
                        Image(systemName: "qrcode.viewfinder")
                    }
                    .accessibilityLabel("Pair a computer")
                }
            }
            .confirmationDialog("Forget \(confirmRemove?.name ?? "")?", isPresented: Binding(
                get: { confirmRemove != nil }, set: { if !$0 { confirmRemove = nil } }), titleVisibility: .visible) {
                Button("Forget", role: .destructive) {
                    if let h = confirmRemove { model.removeHost(h.id) }
                }
            } message: {
                Text("You'll need to scan a new QR code to reconnect. Also remove this phone in Conduit Host to revoke its key.")
            }
        }
    }
}

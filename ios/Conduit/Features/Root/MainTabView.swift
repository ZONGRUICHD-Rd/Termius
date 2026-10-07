import ConduitKit
import SwiftUI

/// Liquid Glass tab bar (system), a bottom accessory that behaves like a "now playing" bar for agents,
/// and the in-app Island when the system Dynamic Island isn't available (LiveContainer, disabled).
struct MainTabView: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        @Bindable var model = model
        TabView(selection: $model.selectedTab) {
            Tab("Agents", systemImage: "sparkles", value: AppTab.sessions) {
                SessionsView()
            }
            Tab("Computers", systemImage: "desktopcomputer", value: AppTab.computers) {
                HostsView()
            }
            Tab("Settings", systemImage: "gearshape", value: AppTab.settings) {
                SettingsView()
            }
        }
        .tabBarMinimizeBehavior(.onScrollDown)
        .tabViewBottomAccessory {
            AgentAccessory()
        }
        .overlay(alignment: .top) {
            if !model.liveActivity.isSupported || !model.liveActivity.isEnabled {
                InAppIsland()
            }
        }
        .sheet(isPresented: $model.showPairing) {
            PairView()
        }
    }
}

/// The tab bar accessory: featured running agent (tap to open), or connection status + quick "new task".
struct AgentAccessory: View {
    @Environment(AppModel.self) private var model
    @Environment(\.tabViewBottomAccessoryPlacement) private var placement

    var body: some View {
        if let store = model.store, let s = store.featured {
            Button {
                model.selectedTab = .sessions
                model.pendingSessionId = s.id
            } label: {
                HStack(spacing: 10) {
                    ZStack {
                        RingProgress(progress: s.progress, color: Brand.stateColor(s.state.rawValue), lineWidth: 2.5)
                        AgentGlyph(agent: s.agent, size: 12)
                    }
                    .frame(width: 24, height: 24)
                    VStack(alignment: .leading, spacing: 0) {
                        Text(s.title).font(.subheadline.weight(.semibold)).lineLimit(1)
                        if placement != .inline {
                            Text(s.detail ?? Brand.stateLabel(s.state.rawValue))
                                .font(.caption).foregroundStyle(.secondary).lineLimit(1)
                                .contentTransition(.opacity)
                        }
                    }
                    Spacer(minLength: 4)
                    if s.state == .waiting {
                        Image(systemName: "hand.raised.fill").foregroundStyle(Brand.amber)
                            .symbolEffect(.bounce, options: .repeat(.periodic(delay: 2)))
                    } else if let start = s.turnStartedAt {
                        ElapsedText(since: start).font(.caption.weight(.medium)).foregroundStyle(.secondary)
                    }
                    if store.active.count > 1 {
                        Text("+\(store.active.count - 1)")
                            .font(.caption2.weight(.bold))
                            .padding(.horizontal, 6).padding(.vertical, 2)
                            .background(Brand.clay.opacity(0.18), in: Capsule())
                    }
                }
                .padding(.horizontal, 14)
                .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .animation(.smooth, value: s)
        } else {
            HStack(spacing: 10) {
                if let client = model.client {
                    ConnectionBadge(status: client.status, rtt: placement == .inline ? nil : client.rttMs)
                } else {
                    Text("No computer").font(.caption.weight(.semibold))
                }
                Spacer()
                if placement != .inline {
                    Text(model.activeHost?.name ?? "").font(.caption).foregroundStyle(.secondary).lineLimit(1)
                }
            }
            .padding(.horizontal, 14)
        }
    }
}

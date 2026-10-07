import ConduitKit
import SwiftUI

/// In-app stand-in for the Dynamic Island, used when Live Activities can't run (LiveContainer has no widget
/// extension; or the user turned them off). A Liquid Glass capsule hugging the top of the screen that morphs
/// into a panel with progress and one-tap approvals.
struct InAppIsland: View {
    @Environment(AppModel.self) private var model
    @Namespace private var glass
    @State private var expanded = false
    @State private var busy = false

    var body: some View {
        if let store = model.store, let s = store.featured {
            GlassEffectContainer(spacing: 12) {
                Group {
                    if expanded {
                        panel(s, store: store)
                            .glassEffect(.regular.tint(Brand.paper.opacity(0.25)).interactive(), in: .rect(cornerRadius: 30))
                            .glassEffectID("island", in: glass)
                    } else {
                        compact(s, count: store.active.count)
                            .glassEffect(.regular.interactive(), in: .capsule)
                            .glassEffectID("island", in: glass)
                    }
                }
            }
            .padding(.horizontal, expanded ? 12 : 0)
            .padding(.top, 2)
            .onTapGesture {
                withAnimation(.bouncy(duration: 0.45)) { expanded.toggle() }
            }
            .sensoryFeedback(.impact(weight: .light), trigger: expanded)
            .sensoryFeedback(.warning, trigger: s.state == .waiting)
            .transition(.scale(scale: 0.6, anchor: .top).combined(with: .opacity))
            .animation(.bouncy, value: s.id)
        }
    }

    private func compact(_ s: SessionSummary, count: Int) -> some View {
        HStack(spacing: 8) {
            ZStack {
                RingProgress(progress: s.progress, color: Brand.stateColor(s.state.rawValue), lineWidth: 2)
                AgentGlyph(agent: s.agent, size: 10)
            }
            .frame(width: 18, height: 18)
            Text(s.state == .waiting ? "Needs approval" : (s.detail ?? Brand.stateLabel(s.state.rawValue)))
                .font(.caption.weight(.semibold))
                .lineLimit(1)
                .frame(maxWidth: 170)
            if count > 1 {
                Text("\(count)").font(.caption2.weight(.bold)).foregroundStyle(Brand.clay)
            }
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 7)
    }

    private func panel(_ s: SessionSummary, store: SessionStore) -> some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack(spacing: 10) {
                AgentAvatar(agent: s.agent, size: 34, state: s.state)
                VStack(alignment: .leading, spacing: 2) {
                    Text(s.title).font(.serif(.headline, weight: .semibold)).lineLimit(1)
                    Text(s.detail ?? Brand.stateLabel(s.state.rawValue)).font(.caption).foregroundStyle(.secondary).lineLimit(2)
                }
                Spacer()
                if let start = s.turnStartedAt, s.state.isActive {
                    ElapsedText(since: start).font(.caption.weight(.semibold)).foregroundStyle(.secondary)
                }
            }
            if let p = s.progress {
                ProgressView(value: p) {
                    EmptyView()
                } currentValueLabel: {
                    Text("\(s.done) of \(s.total) steps").font(.caption2)
                }
                .tint(Brand.stateColor(s.state.rawValue))
            }
            HStack {
                if s.state == .waiting, let approval = store.transcript(for: s.id).pendingApprovals.first {
                    Button("Deny", role: .destructive) { respond(store, s, approval, "deny") }
                        .buttonStyle(.glass)
                    Button("Allow") { respond(store, s, approval, "allow") }
                        .buttonStyle(.glassProminent)
                } else if s.state == .waiting {
                    Button("Review") { open(s) }.buttonStyle(.glassProminent)
                } else {
                    Button("Open") { open(s) }.buttonStyle(.glass)
                    Button("Stop", role: .destructive) { Task { await store.interrupt(s.id) } }.buttonStyle(.glass)
                }
                Spacer()
                if store.active.count > 1 {
                    Text("\(store.active.count) agents running").font(.caption2).foregroundStyle(.secondary)
                }
            }
            .disabled(busy)
        }
        .padding(16)
        .task(id: s.id) { await store.subscribe(s.id) }
    }

    private func open(_ s: SessionSummary) {
        withAnimation(.bouncy) { expanded = false }
        model.selectedTab = .sessions
        model.pendingSessionId = s.id
    }

    private func respond(_ store: SessionStore, _ s: SessionSummary, _ approval: ChatItem, _ decision: String) {
        busy = true
        Task {
            try? await store.respond(s.id, approvalId: String(approval.id.dropFirst(2)), decision: decision)
            busy = false
        }
    }
}

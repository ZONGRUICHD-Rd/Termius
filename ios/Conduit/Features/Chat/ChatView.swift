import ConduitKit
import SwiftUI

struct ChatView: View {
    let store: SessionStore
    let sessionId: String

    @Environment(AppModel.self) private var model
    @AppStorage("showReasoning") private var showReasoning = false
    @State private var draft = ""
    @State private var sendError: String?
    @State private var sending = false
    @State private var confirmDelete = false
    @Environment(\.dismiss) private var dismiss

    private var transcript: Transcript { store.transcript(for: sessionId) }
    private var summary: SessionSummary? { store.summaries[sessionId] }
    private var isActive: Bool { summary?.state.isActive == true }

    var body: some View {
        let t = transcript
        ScrollView {
            LazyVStack(alignment: .leading, spacing: 14) {
                if !t.loaded {
                    ProgressView().frame(maxWidth: .infinity).padding(.top, 40)
                }
                ForEach(t.items) { item in
                    ChatRow(item: item, agent: summary?.agent ?? "", showReasoning: showReasoning)
                        .equatable()
                        .id(item.id)
                }
                if isActive, !t.isStreaming, let s = summary {
                    WorkingIndicator(summary: s)
                        .transition(.opacity)
                }
                Color.clear.frame(height: 4).id("bottom")
            }
            .padding(.horizontal, 16)
            .padding(.top, 12)
            .animation(.smooth(duration: 0.25), value: t.items.count)
        }
        .defaultScrollAnchor(.bottom)
        .defaultScrollAnchor(.bottom, for: .sizeChanges)
        .scrollDismissesKeyboard(.interactively)
        .scrollEdgeEffectStyle(.soft, for: .all)
        .paperBackground()
        .safeAreaInset(edge: .bottom, spacing: 0) {
            ChatDock(store: store, sessionId: sessionId, draft: $draft, sending: $sending, sendError: $sendError)
        }
        .navigationTitle(summary?.title ?? "Session")
        .navigationSubtitle(subtitle)
        .navigationBarTitleDisplayMode(.inline)
        .toolbar {
            ToolbarItem(placement: .topBarTrailing) {
                Menu {
                    Toggle(isOn: $showReasoning) { Label("Show thinking", systemImage: "brain") }
                    if isActive {
                        Button("Interrupt", systemImage: "stop.circle") { Task { await store.interrupt(sessionId) } }
                    }
                    Button("Stop agent process", systemImage: "power") { Task { await store.stop(sessionId) } }
                    Divider()
                    Button("Delete session", systemImage: "trash", role: .destructive) { confirmDelete = true }
                } label: {
                    Image(systemName: "ellipsis")
                }
            }
        }
        .confirmationDialog("Delete this session?", isPresented: $confirmDelete, titleVisibility: .visible) {
            Button("Delete", role: .destructive) {
                Task { await store.delete(sessionId) }
                dismiss()
            }
        }
        .task(id: sessionId) {
            await store.subscribe(sessionId)
        }
        .toolbar(.hidden, for: .tabBar)
    }

    private var subtitle: String {
        guard let s = summary else { return "" }
        return "\(AgentStyle.of(s.agent).name) · \(s.folderName)"
    }
}

private struct WorkingIndicator: View {
    let summary: SessionSummary

    var body: some View {
        HStack(spacing: 10) {
            SparkShape()
                .fill(Brand.clay)
                .frame(width: 14, height: 14)
                .phaseAnimator([0.0, 180.0]) { v, angle in v.rotationEffect(.degrees(angle)) } animation: { _ in .linear(duration: 1.6) }
            Text(summary.detail ?? Brand.stateLabel(summary.state.rawValue))
                .font(.callout)
                .foregroundStyle(Brand.inkSoft)
                .lineLimit(1)
                .contentTransition(.opacity)
                .animation(.smooth, value: summary.detail)
            Spacer()
            if let start = summary.turnStartedAt {
                ElapsedText(since: start).font(.caption).foregroundStyle(.secondary)
            }
        }
        .padding(.vertical, 4)
    }
}

/// Everything floating above the transcript: plan, pending approvals and the composer — Liquid Glass controls.
private struct ChatDock: View {
    let store: SessionStore
    let sessionId: String
    @Binding var draft: String
    @Binding var sending: Bool
    @Binding var sendError: String?

    @Namespace private var glassNS
    @FocusState private var focused: Bool
    @State private var planOpen = false

    private var transcript: Transcript { store.transcript(for: sessionId) }
    private var summary: SessionSummary? { store.summaries[sessionId] }
    private var isActive: Bool { summary?.state.isActive == true }
    private var canSend: Bool { !draft.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty && !sending }

    var body: some View {
        VStack(spacing: 10) {
            if let approval = transcript.pendingApprovals.first {
                ApprovalCard(item: approval, count: transcript.pendingApprovals.count) { decision in
                    Task {
                        try? await store.respond(sessionId, approvalId: String(approval.id.dropFirst(2)), decision: decision)
                    }
                }
                .transition(.move(edge: .bottom).combined(with: .opacity))
            } else if !transcript.plan.isEmpty, isActive || planOpen {
                PlanCard(items: transcript.plan, open: $planOpen)
                    .transition(.move(edge: .bottom).combined(with: .opacity))
            }

            if let sendError {
                Text(sendError).font(.caption).foregroundStyle(Brand.rust).transition(.opacity)
            }

            GlassEffectContainer(spacing: 10) {
                HStack(alignment: .bottom, spacing: 10) {
                    TextField(isActive ? "Steer the agent…" : "Message", text: $draft, axis: .vertical)
                        .lineLimit(1...6)
                        .focused($focused)
                        .padding(.horizontal, 16)
                        .padding(.vertical, 11)
                        .glassEffect(.regular.interactive(), in: .rect(cornerRadius: 22))
                        .glassEffectID("field", in: glassNS)
                        .onSubmit(send)

                    if canSend || !isActive {
                        Button(action: send) {
                            Image(systemName: sending ? "ellipsis" : "arrow.up")
                                .font(.body.weight(.bold))
                                .frame(width: 26, height: 26)
                                .symbolEffect(.variableColor.iterative, isActive: sending)
                        }
                        .buttonStyle(.glassProminent)
                        .buttonBorderShape(.circle)
                        .tint(Brand.clay)
                        .disabled(!canSend)
                        .glassEffectID("action", in: glassNS)
                        .sensoryFeedback(.impact(weight: .medium), trigger: sending) { _, new in new }
                    } else {
                        Button {
                            Task { await store.interrupt(sessionId) }
                        } label: {
                            Image(systemName: "stop.fill")
                                .font(.body.weight(.bold))
                                .frame(width: 26, height: 26)
                        }
                        .buttonStyle(.glass)
                        .buttonBorderShape(.circle)
                        .glassEffectID("action", in: glassNS)
                        .accessibilityLabel("Interrupt")
                    }
                }
            }
        }
        .padding(.horizontal, 12)
        .padding(.top, 8)
        .padding(.bottom, 8)
        .animation(.bouncy(duration: 0.35), value: canSend)
        .animation(.bouncy(duration: 0.35), value: isActive)
        .animation(.smooth, value: transcript.pendingApprovals.map(\.id))
    }

    private func send() {
        let text = draft.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !text.isEmpty, !sending else { return }
        sending = true
        sendError = nil
        draft = ""
        Task {
            do {
                try await store.send(sessionId, text: text)
            } catch {
                draft = text
                sendError = error.localizedDescription
            }
            sending = false
        }
    }
}

private struct ApprovalCard: View {
    let item: ChatItem
    let count: Int
    let respond: (String) -> Void
    @State private var expanded = false

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack(spacing: 8) {
                Image(systemName: icon).foregroundStyle(Brand.amber)
                Text(item.title ?? "Approval needed").font(.headline).lineLimit(2)
                Spacer()
                if count > 1 { Text("1 of \(count)").font(.caption).foregroundStyle(.secondary) }
            }
            if !item.text.isEmpty {
                Text(item.text)
                    .font(.caption.monospaced())
                    .foregroundStyle(Brand.inkSoft)
                    .lineLimit(expanded ? 30 : 3)
                    .onTapGesture { withAnimation(.snappy) { expanded.toggle() } }
            }
            HStack(spacing: 8) {
                Button("Deny", role: .destructive) { respond("deny") }
                    .buttonStyle(.glass)
                Spacer()
                if item.canAlways {
                    Button("Always") { respond("allow_always") }
                        .buttonStyle(.glass)
                }
                Button("Allow") { respond("allow") }
                    .buttonStyle(.glassProminent)
                    .tint(Brand.clay)
            }
            .controlSize(.regular)
        }
        .padding(16)
        .glassEffect(.regular.tint(Brand.amber.opacity(0.18)), in: .rect(cornerRadius: 26))
        .sensoryFeedback(.warning, trigger: item.id)
    }

    private var icon: String {
        switch item.approvalKind {
        case "command": return "terminal.fill"
        case "edit": return "pencil.and.list.clipboard"
        case "network": return "network"
        default: return "hand.raised.fill"
        }
    }
}

private struct PlanCard: View {
    let items: [PlanItem]
    @Binding var open: Bool

    private var done: Int { items.filter { $0.status == "completed" }.count }
    private var current: PlanItem? { items.first { $0.status == "in_progress" } }

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Button {
                withAnimation(.snappy) { open.toggle() }
            } label: {
                HStack(spacing: 10) {
                    RingProgress(progress: items.isEmpty ? 0 : Double(done) / Double(items.count), color: Brand.clay, lineWidth: 3)
                        .frame(width: 20, height: 20)
                    VStack(alignment: .leading, spacing: 1) {
                        Text(current?.text ?? (done == items.count ? "All steps done" : "Plan"))
                            .font(.subheadline.weight(.semibold)).lineLimit(1)
                        Text("\(done) of \(items.count) steps").font(.caption).foregroundStyle(.secondary)
                    }
                    Spacer()
                    Image(systemName: "chevron.up").font(.caption.weight(.bold)).foregroundStyle(.secondary)
                        .rotationEffect(.degrees(open ? 180 : 0))
                }
                .contentShape(Rectangle())
            }
            .buttonStyle(.plain)

            if open {
                VStack(alignment: .leading, spacing: 8) {
                    ForEach(items) { item in
                        HStack(alignment: .firstTextBaseline, spacing: 8) {
                            Image(systemName: item.status == "completed" ? "checkmark.circle.fill"
                                  : item.status == "in_progress" ? "circle.dotted.circle" : "circle")
                                .foregroundStyle(item.status == "pending" ? Color.secondary : Brand.clay)
                                .font(.caption)
                            Text(item.text)
                                .font(.subheadline)
                                .strikethrough(item.status == "completed", color: .secondary)
                                .foregroundStyle(item.status == "completed" ? Color.secondary : Brand.ink)
                        }
                    }
                }
                .transition(.opacity)
            }
        }
        .padding(14)
        .glassEffect(.regular, in: .rect(cornerRadius: 24))
    }
}

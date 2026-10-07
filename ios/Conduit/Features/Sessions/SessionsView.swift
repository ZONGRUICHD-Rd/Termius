import ConduitKit
import SwiftUI

struct SessionsView: View {
    @Environment(AppModel.self) private var model
    @State private var path: [String] = []
    @State private var showNew = false
    @State private var query = ""

    var body: some View {
        NavigationStack(path: $path) {
            Group {
                if let store = model.store, let client = model.client {
                    SessionList(store: store, client: client, query: query, showNew: $showNew)
                } else {
                    ContentUnavailableView("No computer selected", systemImage: "desktopcomputer",
                                           description: Text("Pair a computer to start."))
                }
            }
            .navigationTitle("Agents")
            .navigationDestination(for: String.self) { id in
                if let store = model.store {
                    ChatView(store: store, sessionId: id)
                }
            }
            .searchable(text: $query, placement: .navigationBarDrawer(displayMode: .automatic), prompt: "Search sessions")
            .toolbar {
                ToolbarItem(placement: .topBarLeading) {
                    if let client = model.client {
                        Menu {
                            ForEach(model.hosts) { host in
                                Button {
                                    model.connect(to: host.id)
                                } label: {
                                    Label(host.name, systemImage: host.id == model.activeHostId ? "checkmark" : "desktopcomputer")
                                }
                            }
                            Divider()
                            Button("Pair another computer", systemImage: "qrcode.viewfinder") { model.showPairing = true }
                        } label: {
                            ConnectionBadge(status: client.status)
                                .labelStyle(.titleAndIcon)
                        }
                    }
                }
                ToolbarSpacer(.flexible, placement: .topBarTrailing)
                ToolbarItem(placement: .topBarTrailing) {
                    Button {
                        showNew = true
                    } label: {
                        Image(systemName: "plus")
                    }
                    .buttonStyle(.glassProminent)
                    .tint(Brand.clay)
                    .disabled(model.client?.status.isConnected != true)
                    .accessibilityLabel("New session")
                }
            }
            .sheet(isPresented: $showNew) {
                if let store = model.store, let client = model.client {
                    NewSessionSheet(store: store, client: client) { session in
                        path = [session.id]
                    }
                }
            }
        }
        .onChange(of: model.pendingSessionId) { _, id in
            guard let id else { return }
            path = [id]
            model.pendingSessionId = nil
        }
        .onAppear {
            if let id = model.pendingSessionId {
                path = [id]
                model.pendingSessionId = nil
            }
        }
    }
}

private struct SessionList: View {
    let store: SessionStore
    let client: ConduitClient
    let query: String
    @Binding var showNew: Bool

    private var filtered: [SessionSummary] {
        let all = store.sorted
        guard !query.isEmpty else { return all }
        return all.filter { $0.title.localizedCaseInsensitiveContains(query) || $0.cwd.localizedCaseInsensitiveContains(query)
            || $0.agent.localizedCaseInsensitiveContains(query) }
    }

    var body: some View {
        let sessions = filtered
        List {
            if case .failed(let message) = client.status {
                Section {
                    Label(message, systemImage: "exclamationmark.triangle.fill").foregroundStyle(Brand.rust)
                }
            }
            let active = sessions.filter { $0.state.isActive }
            if !active.isEmpty {
                Section {
                    ForEach(active) { s in row(s) }
                } header: {
                    EyebrowLabel(text: "Working now")
                }
            }
            let recent = sessions.filter { !$0.state.isActive }
            if !recent.isEmpty {
                Section {
                    ForEach(recent) { s in row(s) }
                } header: {
                    EyebrowLabel(text: "Recent")
                }
            }
        }
        .listStyle(.insetGrouped)
        .scrollContentBackground(.hidden)
        .paperBackground()
        .refreshable { await store.refresh() }
        .overlay {
            if store.loadedOnce && sessions.isEmpty && query.isEmpty {
                EmptySessions(showNew: $showNew)
            } else if !store.loadedOnce && !client.status.isConnected {
                ProgressView().controlSize(.large)
            }
        }
        .animation(.smooth, value: sessions.map(\.id))
    }

    private func row(_ s: SessionSummary) -> some View {
        NavigationLink(value: s.id) {
            SessionRow(session: s)
        }
        .listRowBackground(Brand.paperDeep.opacity(0.6))
        .swipeActions(edge: .trailing) {
            Button(role: .destructive) {
                Task { await store.delete(s.id) }
            } label: {
                Label("Delete", systemImage: "trash")
            }
            if s.state.isActive {
                Button {
                    Task { await store.interrupt(s.id) }
                } label: {
                    Label("Stop", systemImage: "stop.fill")
                }
                .tint(Brand.amber)
            }
        }
    }
}

struct SessionRow: View {
    let session: SessionSummary

    var body: some View {
        HStack(alignment: .top, spacing: 12) {
            AgentAvatar(agent: session.agent, size: 38, state: session.state)
            VStack(alignment: .leading, spacing: 4) {
                HStack(alignment: .firstTextBaseline) {
                    Text(session.title)
                        .font(.serif(.body, weight: .semibold))
                        .foregroundStyle(Brand.ink)
                        .lineLimit(1)
                    Spacer(minLength: 6)
                    Text(session.updatedAt.relative).font(.caption).foregroundStyle(.secondary)
                }
                Text(session.state.isActive ? (session.detail ?? "Working…") : session.preview)
                    .font(.subheadline)
                    .foregroundStyle(Brand.inkSoft)
                    .lineLimit(2)
                    .contentTransition(.opacity)
                HStack(spacing: 8) {
                    StatePill(state: session.state, approvals: session.approvals)
                    Label(session.folderName, systemImage: "folder")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                        .lineLimit(1)
                    if let p = session.progress, session.state.isActive {
                        Text("\(Int(p * 100))%").font(.caption.monospacedDigit().weight(.semibold)).foregroundStyle(Brand.clay)
                    }
                }
            }
        }
        .padding(.vertical, 4)
    }
}

private struct EmptySessions: View {
    @Binding var showNew: Bool

    var body: some View {
        VStack(spacing: 16) {
            SparkShape().fill(Brand.clay.opacity(0.85)).frame(width: 44, height: 44)
            Text("Start your first task").font(.serif(.title2, weight: .medium))
            Text("Pick an agent and a project folder on your computer.\nYou'll see progress here and in the Dynamic Island.")
                .font(.subheadline).foregroundStyle(.secondary).multilineTextAlignment(.center)
            Button("New session") { showNew = true }
                .buttonStyle(.glassProminent)
                .tint(Brand.clay)
        }
        .padding(32)
    }
}

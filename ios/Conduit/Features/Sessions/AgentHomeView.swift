import ConduitKit
import PhotosUI
import SwiftUI

/// Home of one agent: its conversations, and a composer at the bottom to start a new task right away
/// (pick a project, a mode, attach screenshots, type, send → straight into the chat).
struct AgentHomeView: View {
    let agent: String

    @Environment(AppModel.self) private var model
    @State private var path: [String] = []
    @State private var query = ""
    @State private var showHistory = false

    var body: some View {
        NavigationStack(path: $path) {
            Group {
                if let store = model.store, let client = model.client {
                    AgentSessionList(agent: agent, store: store, client: client, query: query)
                        .safeAreaInset(edge: .bottom, spacing: 0) {
                            if store.agentInfo(agent)?.available == true {
                                NewTaskComposer(agent: agent, store: store) { path = [$0.id] }
                            }
                        }
                } else {
                    ContentUnavailableView("还没有连接电脑", systemImage: "desktopcomputer",
                                           description: Text("在“设置”中配对一台电脑。"))
                }
            }
            .navigationTitle(AgentStyle.of(agent).name)
            .navigationDestination(for: String.self) { id in
                if let store = model.store { ChatView(store: store, sessionId: id) }
            }
            .searchable(text: $query, prompt: "搜索对话")
            .toolbar {
                ToolbarItem(placement: .topBarLeading) { HostMenu() }
                if model.store?.agentInfo(agent)?.supportsHistory == true {
                    ToolbarItem(placement: .topBarTrailing) {
                        Button {
                            showHistory = true
                        } label: {
                            Image(systemName: "clock.arrow.circlepath")
                        }
                        .accessibilityLabel("电脑上的对话")
                    }
                }
            }
            .sheet(isPresented: $showHistory) {
                if let store = model.store {
                    HistoryImportView(agent: agent, store: store) { path = [$0.id] }
                }
            }
        }
        .onChange(of: model.pendingSessionId) { _, _ in consumePending() }
        .onAppear(perform: consumePending)
    }

    private func consumePending() {
        guard let id = model.pendingSessionId, model.selectedTab == .agent(agent) else { return }
        path = [id]
        model.pendingSessionId = nil
    }
}

/// Connection status + computer switcher in the navigation bar.
struct HostMenu: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        if let client = model.client {
            Menu {
                Section(client.host.name) {
                    if let route = client.host.lastRoute.flatMap(RouteKind.init(rawValue:)), client.status.isConnected {
                        Label("通过 \(route.label) 连接", systemImage: route.symbol)
                    }
                    Button("重新连接", systemImage: "arrow.clockwise") { client.reconnect() }
                }
                if model.hosts.count > 1 {
                    Section("切换电脑") {
                        ForEach(model.hosts) { host in
                            Button {
                                model.connect(to: host.id)
                            } label: {
                                Label(host.name, systemImage: host.id == model.activeHostId ? "checkmark" : "desktopcomputer")
                            }
                        }
                    }
                }
                Button("配对新电脑", systemImage: "qrcode.viewfinder") { model.showPairing = true }
            } label: {
                ConnectionBadge(status: client.status)
            }
        }
    }
}

private struct AgentSessionList: View {
    let agent: String
    let store: SessionStore
    let client: ConduitClient
    let query: String

    private var sessions: [SessionSummary] {
        let all = store.sessions(for: agent)
        guard !query.isEmpty else { return all }
        return all.filter { $0.title.localizedCaseInsensitiveContains(query) || $0.cwd.localizedCaseInsensitiveContains(query) }
    }

    var body: some View {
        let list = sessions
        let info = store.agentInfo(agent)
        List {
            if case .failed(let message) = client.status {
                Section { Label(message, systemImage: "exclamationmark.triangle.fill").foregroundStyle(Brand.rust) }
            }
            if let info, !info.available {
                Section {
                    VStack(alignment: .leading, spacing: 6) {
                        Text("这台电脑上没有安装 \(info.name)").font(.headline)
                        Text(info.detail).font(.footnote).foregroundStyle(.secondary)
                    }
                    .padding(.vertical, 4)
                }
            }
            let active = list.filter { $0.state.isActive }
            if !active.isEmpty {
                Section { ForEach(active) { row($0) } } header: { EyebrowLabel(text: "进行中") }
            }
            let recent = list.filter { !$0.state.isActive }
            if !recent.isEmpty {
                Section { ForEach(recent) { row($0) } } header: { EyebrowLabel(text: "最近") }
            }
        }
        .listStyle(.insetGrouped)
        .scrollContentBackground(.hidden)
        .paperBackground()
        .refreshable { await store.refresh() }
        .overlay {
            if store.loadedOnce && list.isEmpty && query.isEmpty && info?.available != false {
                AgentEmptyState(agent: agent)
            } else if !store.loadedOnce && !client.status.isConnected {
                ProgressView().controlSize(.large)
            }
        }
        .animation(.smooth, value: list.map(\.id))
    }

    private func row(_ s: SessionSummary) -> some View {
        NavigationLink(value: s.id) {
            SessionRow(session: s)
        }
        .listRowBackground(Brand.paperDeep.opacity(0.6))
        .swipeActions(edge: .trailing) {
            Button(role: .destructive) {
                Task { await store.delete(s.id) }
            } label: { Label("删除", systemImage: "trash") }
            if s.state.isActive {
                Button {
                    Task { await store.interrupt(s.id) }
                } label: { Label("停止", systemImage: "stop.fill") }
                .tint(Brand.amber)
            }
        }
    }
}

struct SessionRow: View {
    let session: SessionSummary

    var body: some View {
        VStack(alignment: .leading, spacing: 5) {
            HStack(alignment: .firstTextBaseline) {
                Text(session.title)
                    .font(.body.weight(.semibold))
                    .foregroundStyle(Brand.ink)
                    .lineLimit(1)
                Spacer(minLength: 6)
                Text(session.updatedAt.relative).font(.caption).foregroundStyle(.secondary)
            }
            Text(session.state.isActive ? (session.detail ?? "工作中…") : session.preview)
                .font(.subheadline)
                .foregroundStyle(Brand.inkSoft)
                .lineLimit(2)
                .contentTransition(.opacity)
            HStack(spacing: 8) {
                if session.state != .idle && session.state != .stopped || session.approvals > 0 {
                    StatePill(state: session.state, approvals: session.approvals)
                }
                Label(session.folderName, systemImage: "folder")
                    .font(.caption).foregroundStyle(.secondary).lineLimit(1)
                if let branch = session.branch, !branch.isEmpty {
                    Label(branch, systemImage: "arrow.triangle.branch")
                        .font(.caption).foregroundStyle(.secondary).lineLimit(1)
                }
                Spacer(minLength: 0)
                DiffStat(added: session.added, deleted: session.deleted)
            }
        }
        .padding(.vertical, 4)
    }
}

/// "+12 −3" in green/red, like Codex and GitHub.
struct DiffStat: View {
    let added: Int
    let deleted: Int

    var body: some View {
        if added > 0 || deleted > 0 {
            HStack(spacing: 4) {
                Text("+\(added)").foregroundStyle(Brand.olive)
                Text("−\(deleted)").foregroundStyle(Brand.rust)
            }
            .font(.caption.monospacedDigit().weight(.semibold))
        }
    }
}

private struct AgentEmptyState: View {
    let agent: String

    var body: some View {
        VStack(spacing: 14) {
            AgentGlyph(agent: agent, size: 44)
            Text("和 \(AgentStyle.of(agent).name) 开始一个任务").font(.serif(.title2, weight: .medium))
            Text("在下方选择项目文件夹，描述你想做的事。\n进度会显示在灵动岛上。")
                .font(.subheadline).foregroundStyle(.secondary).multilineTextAlignment(.center)
        }
        .padding(32)
        .padding(.bottom, 120)
    }
}

/// Bottom composer that starts a new conversation (Claude Code mobile style).
struct NewTaskComposer: View {
    let agent: String
    let store: SessionStore
    var onCreated: (SessionSummary) -> Void

    @AppStorage private var folder: String
    @AppStorage private var mode: String
    @State private var text = ""
    @State private var attachments: [Attachment] = []
    @State private var pickerItems: [PhotosPickerItem] = []
    @State private var recents: [String] = []
    @State private var showFolders = false
    @State private var sending = false
    @State private var error: String?
    @FocusState private var focused: Bool

    init(agent: String, store: SessionStore, onCreated: @escaping (SessionSummary) -> Void) {
        self.agent = agent
        self.store = store
        self.onCreated = onCreated
        _folder = AppStorage(wrappedValue: "", "lastFolder.\(agent)")
        _mode = AppStorage(wrappedValue: "", "lastMode.\(agent)")
    }

    private var info: AgentInfo? { store.agentInfo(agent) }
    private var canSend: Bool { !folder.isEmpty && (!text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty || !attachments.isEmpty) && !sending }

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            if let error {
                Text(error).font(.caption).foregroundStyle(Brand.rust).padding(.horizontal, 6)
            }
            if !attachments.isEmpty {
                AttachmentStrip(attachments: $attachments)
            }
            GlassEffectContainer(spacing: 8) {
                VStack(alignment: .leading, spacing: 10) {
                    TextField("让 \(AgentStyle.of(agent).name) 做点什么…", text: $text, axis: .vertical)
                        .lineLimit(1...6)
                        .focused($focused)
                        .submitLabel(.send)
                    HStack(spacing: 8) {
                        PhotosPicker(selection: $pickerItems, maxSelectionCount: 4, matching: .images) {
                            Image(systemName: "plus").font(.body.weight(.semibold)).frame(width: 30, height: 30)
                        }
                        .buttonStyle(.glass)
                        .buttonBorderShape(.circle)

                        Menu {
                            if !recents.isEmpty {
                                Section("最近使用") {
                                    ForEach(recents, id: \.self) { r in
                                        Button(Self.lastComponent(r)) { folder = r }
                                    }
                                }
                            }
                            Button("浏览电脑上的文件夹…", systemImage: "folder") { showFolders = true }
                        } label: {
                            ChipLabel(icon: "folder", text: folder.isEmpty ? "选择项目" : Self.lastComponent(folder),
                                      highlighted: folder.isEmpty)
                        }

                        if let modes = info?.modes, !modes.isEmpty {
                            Menu {
                                Picker("模式", selection: $mode) {
                                    ForEach(modes) { m in
                                        Text(m.label).tag(m.id == "default" ? "" : m.id)
                                    }
                                }
                            } label: {
                                ChipLabel(icon: "dial.medium", text: modes.first { $0.id == mode }?.label ?? "默认", highlighted: false)
                            }
                        }
                        Spacer(minLength: 0)
                        Button(action: send) {
                            Image(systemName: sending ? "ellipsis" : "arrow.up")
                                .font(.body.weight(.bold))
                                .frame(width: 30, height: 30)
                                .symbolEffect(.variableColor.iterative, isActive: sending)
                        }
                        .buttonStyle(.glassProminent)
                        .buttonBorderShape(.circle)
                        .tint(Brand.clay)
                        .disabled(!canSend)
                    }
                }
                .padding(14)
                .glassEffect(.regular.interactive(), in: .rect(cornerRadius: 26))
            }
        }
        .padding(.horizontal, 12)
        .padding(.bottom, 8)
        .task { await loadRecents() }
        .onChange(of: pickerItems) { _, items in Task { await load(items) } }
        .sheet(isPresented: $showFolders) {
            NavigationStack { FolderPicker(store: store, selection: $folder) }
                .presentationDetents([.large])
        }
        .animation(.smooth, value: attachments.count)
    }

    static func lastComponent(_ path: String) -> String {
        path.split(whereSeparator: { $0 == "/" || $0 == "\\" }).last.map(String.init) ?? path
    }

    private func loadRecents() async {
        if let listing = try? await store.listDirectory(nil) {
            recents = listing.recents
            if folder.isEmpty, let first = listing.recents.first { folder = first }
        }
    }

    private func load(_ items: [PhotosPickerItem]) async {
        for item in items {
            if let data = try? await item.loadTransferable(type: Data.self), let a = Attachment(imageData: data) {
                attachments.append(a)
            }
        }
        pickerItems = []
    }

    private func send() {
        guard canSend else { return }
        sending = true
        error = nil
        let prompt = text.trimmingCharacters(in: .whitespacesAndNewlines)
        let images = attachments
        Task {
            do {
                let s = try await store.create(agent: agent, cwd: folder, prompt: prompt,
                                               mode: mode.isEmpty ? nil : mode, images: images)
                text = ""
                attachments = []
                focused = false
                onCreated(s)
            } catch {
                self.error = error.localizedDescription
            }
            sending = false
        }
    }
}

/// Small rounded label used for the composer's project / mode / model pickers.
struct ChipLabel: View {
    let icon: String
    let text: String
    var highlighted = false

    var body: some View {
        HStack(spacing: 5) {
            Image(systemName: icon).font(.caption.weight(.semibold))
            Text(text).font(.caption.weight(.semibold)).lineLimit(1)
            Image(systemName: "chevron.down").font(.system(size: 8, weight: .bold))
        }
        .foregroundStyle(highlighted ? Brand.clay : Brand.ink)
        .padding(.horizontal, 10)
        .padding(.vertical, 7)
        .glassEffect(highlighted ? .regular.tint(Brand.clay.opacity(0.2)).interactive() : .regular.interactive(), in: .capsule)
    }
}

/// Thumbnails of images queued in a composer.
struct AttachmentStrip: View {
    @Binding var attachments: [Attachment]

    var body: some View {
        ScrollView(.horizontal, showsIndicators: false) {
            HStack(spacing: 8) {
                ForEach(attachments) { a in
                    Image(uiImage: a.thumbnail)
                        .resizable()
                        .scaledToFill()
                        .frame(width: 58, height: 58)
                        .clipShape(.rect(cornerRadius: 12))
                        .overlay(alignment: .topTrailing) {
                            Button {
                                attachments.removeAll { $0.id == a.id }
                            } label: {
                                Image(systemName: "xmark.circle.fill").symbolRenderingMode(.palette)
                                    .foregroundStyle(.white, .black.opacity(0.6))
                            }
                            .offset(x: 5, y: -5)
                        }
                }
            }
            .padding(.horizontal, 6)
            .padding(.top, 6)
        }
    }
}

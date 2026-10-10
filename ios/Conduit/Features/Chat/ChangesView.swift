import ConduitKit
import SwiftUI

/// Review what the agent changed and ship it: files with +/−, per-file diffs, commit, push, branch, pull request.
struct ChangesView: View {
    let store: SessionStore
    let sessionId: String

    @Environment(\.dismiss) private var dismiss
    @Environment(\.openURL) private var openURL
    @State private var status: GitStatus?
    @State private var error: String?
    @State private var busy: String?
    @State private var toast: String?
    @State private var showCommit = false
    @State private var showPR = false
    @State private var showBranch = false
    @State private var branchName = ""

    private var summary: SessionSummary? { store.summaries[sessionId] }

    var body: some View {
        NavigationStack {
            List {
                if let status {
                    Section {
                        HStack(spacing: 12) {
                            Image(systemName: "arrow.triangle.branch").foregroundStyle(Brand.clay)
                            VStack(alignment: .leading, spacing: 2) {
                                Text(status.branch.isEmpty ? "（无分支）" : status.branch).font(.headline.monospaced())
                                Text(syncText(status)).font(.caption).foregroundStyle(.secondary)
                            }
                            Spacer()
                            DiffStat(added: status.added, deleted: status.deleted)
                        }
                    }
                    if status.files.isEmpty {
                        Section {
                            Label("没有未提交的修改", systemImage: "checkmark.circle").foregroundStyle(Brand.olive)
                        }
                    } else {
                        Section {
                            ForEach(status.files) { f in
                                NavigationLink {
                                    DiffView(store: store, sessionId: sessionId, file: f)
                                } label: {
                                    FileRow(file: f)
                                }
                                .swipeActions {
                                    Button("撤销修改", role: .destructive) { discard(f) }
                                }
                            }
                        } header: {
                            EyebrowLabel(text: "\(status.files.count) 个文件有修改")
                        }
                        Section {
                            NavigationLink("查看全部差异") { DiffView(store: store, sessionId: sessionId, file: nil) }
                        }
                    }
                } else if let error {
                    Label(error, systemImage: "exclamationmark.triangle.fill").foregroundStyle(Brand.rust)
                }
            }
            .overlay { if status == nil && error == nil { ProgressView() } }
            .scrollContentBackground(.hidden)
            .paperBackground()
            .refreshable { await load() }
            .navigationTitle("代码变更")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button(role: .cancel) { dismiss() } label: { Image(systemName: "xmark") }
                }
                ToolbarItem(placement: .topBarTrailing) {
                    Menu {
                        Button("新建分支…", systemImage: "arrow.triangle.branch") { showBranch = true }
                        Button("推送到远程", systemImage: "arrow.up.circle") { run("推送") { try await store.gitPush(sessionId) } }
                    } label: {
                        Image(systemName: "ellipsis")
                    }
                }
            }
            .safeAreaInset(edge: .bottom) {
                if let status, status.isRepo {
                    ShipBar(status: status, busy: busy) { showCommit = true } onPR: { showPR = true }
                }
            }
            .overlay(alignment: .top) {
                if let toast {
                    Text(toast)
                        .font(.subheadline.weight(.semibold))
                        .padding(.horizontal, 16).padding(.vertical, 10)
                        .glassEffect(.regular.tint(Brand.olive.opacity(0.25)), in: .capsule)
                        .transition(.move(edge: .top).combined(with: .opacity))
                        .padding(.top, 6)
                }
            }
            .animation(.bouncy, value: toast)
            .sheet(isPresented: $showCommit) {
                CommitSheet(defaultMessage: summary?.title ?? "") { message, push in
                    run(push ? "提交并推送" : "提交") {
                        _ = try await store.gitCommit(sessionId, message: message)
                        if push { try await store.gitPush(sessionId) }
                    }
                }
                .presentationDetents([.medium])
            }
            .sheet(isPresented: $showPR) {
                PullRequestSheet(defaultTitle: summary?.title ?? "", hasGh: status?.hasGh ?? false,
                                 hasChanges: !(status?.files.isEmpty ?? true)) { title, body, draft in
                    run("创建 PR") {
                        if !(status?.files.isEmpty ?? true) { _ = try await store.gitCommit(sessionId, message: title) }
                        let url = try await store.gitPullRequest(sessionId, title: title, body: body, draft: draft)
                        if let u = URL(string: url) { openURL(u) }
                    }
                }
                .presentationDetents([.medium, .large])
            }
            .alert("新建分支", isPresented: $showBranch) {
                TextField("feature/my-change", text: $branchName)
                    .textInputAutocapitalization(.never).autocorrectionDisabled()
                Button("取消", role: .cancel) {}
                Button("创建") { run("新建分支") { try await store.gitBranch(sessionId, name: branchName) } }
            }
            .task { await load() }
        }
    }

    private func syncText(_ s: GitStatus) -> String {
        if s.upstream.isEmpty { return "尚未推送到远程" }
        var parts: [String] = [s.upstream]
        if s.ahead > 0 { parts.append("领先 \(s.ahead) 个提交") }
        if s.behind > 0 { parts.append("落后 \(s.behind) 个提交") }
        if s.ahead == 0 && s.behind == 0 { parts.append("已同步") }
        return parts.joined(separator: " · ")
    }

    private func load() async {
        do {
            let s = try await store.gitStatus(sessionId)
            if !s.isRepo {
                error = "这个项目不是 Git 仓库"
                status = nil
            } else {
                status = s
                error = nil
            }
        } catch {
            self.error = error.localizedDescription
        }
    }

    private func discard(_ f: GitStatus.File) {
        run("撤销") { try await store.gitDiscard(sessionId, path: f.path) }
    }

    private func run(_ label: String, _ action: @escaping () async throws -> Void) {
        busy = label
        Task {
            do {
                try await action()
                toast = "\(label)完成"
                Task {
                    try? await Task.sleep(nanoseconds: 1_800_000_000)
                    toast = nil
                }
            } catch {
                self.error = error.localizedDescription
            }
            busy = nil
            await load()
        }
    }
}

private struct FileRow: View {
    let file: GitStatus.File

    var body: some View {
        HStack(spacing: 10) {
            Image(systemName: icon).foregroundStyle(color).frame(width: 18)
            VStack(alignment: .leading, spacing: 1) {
                Text(file.name).font(.callout.monospaced()).foregroundStyle(Brand.ink).lineLimit(1)
                if !file.directory.isEmpty {
                    Text(file.directory).font(.caption2.monospaced()).foregroundStyle(.secondary).lineLimit(1).truncationMode(.head)
                }
            }
            Spacer()
            DiffStat(added: file.added, deleted: file.deleted)
        }
    }

    private var icon: String {
        switch file.status {
        case "added": return "plus.square.fill"
        case "deleted": return "minus.square.fill"
        case "renamed": return "arrow.right.square.fill"
        default: return "pencil.circle.fill"
        }
    }

    private var color: Color {
        switch file.status {
        case "added": return Brand.olive
        case "deleted": return Brand.rust
        default: return Brand.amber
        }
    }
}

/// Floating glass bar with the two primary actions.
private struct ShipBar: View {
    let status: GitStatus
    let busy: String?
    var onCommit: () -> Void
    var onPR: () -> Void

    var body: some View {
        GlassEffectContainer(spacing: 10) {
            HStack(spacing: 10) {
                if let busy {
                    HStack(spacing: 8) {
                        ProgressView()
                        Text("正在\(busy)…").font(.subheadline.weight(.semibold))
                    }
                    .frame(maxWidth: .infinity)
                    .padding(.vertical, 12)
                    .glassEffect(.regular, in: .capsule)
                } else {
                    Button(action: onCommit) {
                        Label("提交", systemImage: "checkmark.circle").frame(maxWidth: .infinity)
                    }
                    .buttonStyle(.glass)
                    .disabled(status.files.isEmpty)
                    Button(action: onPR) {
                        Label("创建 PR", systemImage: "arrow.triangle.pull").frame(maxWidth: .infinity)
                    }
                    .buttonStyle(.glassProminent)
                    .tint(Brand.clay)
                }
            }
            .controlSize(.large)
        }
        .padding(.horizontal, 16)
        .padding(.bottom, 6)
    }
}

private struct CommitSheet: View {
    let defaultMessage: String
    var onCommit: (String, Bool) -> Void
    @Environment(\.dismiss) private var dismiss
    @State private var message = ""
    @State private var push = true

    var body: some View {
        NavigationStack {
            Form {
                TextField("提交说明", text: $message, axis: .vertical).lineLimit(2...6)
                Toggle("提交后推送到远程", isOn: $push)
            }
            .scrollContentBackground(.hidden)
            .paperBackground()
            .navigationTitle("提交修改")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) { Button("取消", role: .cancel) { dismiss() } }
                ToolbarItem(placement: .confirmationAction) {
                    Button("提交") {
                        onCommit(message, push)
                        dismiss()
                    }
                    .buttonStyle(.glassProminent).tint(Brand.clay)
                    .disabled(message.trimmingCharacters(in: .whitespaces).isEmpty)
                }
            }
            .onAppear { if message.isEmpty { message = defaultMessage } }
        }
    }
}

private struct PullRequestSheet: View {
    let defaultTitle: String
    let hasGh: Bool
    let hasChanges: Bool
    var onCreate: (String, String, Bool) -> Void
    @Environment(\.dismiss) private var dismiss
    @State private var title = ""
    @State private var bodyText = ""
    @State private var draft = false

    var body: some View {
        NavigationStack {
            Form {
                if !hasGh {
                    Label("电脑上需要安装并登录 GitHub CLI（gh）才能创建 PR。", systemImage: "exclamationmark.triangle")
                        .foregroundStyle(Brand.amber)
                }
                Section {
                    TextField("标题", text: $title)
                    TextField("说明（可选）", text: $bodyText, axis: .vertical).lineLimit(3...8)
                    Toggle("创建为草稿", isOn: $draft)
                } footer: {
                    Text(hasChanges ? "会先提交当前所有修改，推送分支，再创建 PR。" : "会推送当前分支并创建 PR。")
                }
            }
            .scrollContentBackground(.hidden)
            .paperBackground()
            .navigationTitle("创建 Pull Request")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) { Button("取消", role: .cancel) { dismiss() } }
                ToolbarItem(placement: .confirmationAction) {
                    Button("创建") {
                        onCreate(title, bodyText, draft)
                        dismiss()
                    }
                    .buttonStyle(.glassProminent).tint(Brand.clay)
                    .disabled(title.trimmingCharacters(in: .whitespaces).isEmpty || !hasGh)
                }
            }
            .onAppear { if title.isEmpty { title = defaultTitle } }
        }
    }
}

/// Unified diff with added / removed lines tinted, hunk headers highlighted.
struct DiffView: View {
    let store: SessionStore
    let sessionId: String
    let file: GitStatus.File?

    @State private var lines: [String] = []
    @State private var truncated = false
    @State private var error: String?
    @State private var loaded = false

    var body: some View {
        ScrollView([.vertical, .horizontal]) {
            LazyVStack(alignment: .leading, spacing: 0) {
                ForEach(Array(lines.enumerated()), id: \.offset) { _, line in
                    Text(line.isEmpty ? " " : line)
                        .font(.system(.caption, design: .monospaced))
                        .foregroundStyle(color(line))
                        .padding(.horizontal, 10)
                        .padding(.vertical, 1)
                        .frame(minWidth: 0, maxWidth: .infinity, alignment: .leading)
                        .background(background(line))
                }
                if truncated {
                    Text("差异过大，已截断。").font(.caption).foregroundStyle(.secondary).padding()
                }
            }
            .textSelection(.enabled)
        }
        .overlay {
            if !loaded { ProgressView() }
            else if let error { Text(error).foregroundStyle(Brand.rust) }
            else if lines.isEmpty { ContentUnavailableView("没有差异", systemImage: "equal.circle") }
        }
        .background(Brand.paper)
        .navigationTitle(file?.name ?? "全部差异")
        .navigationBarTitleDisplayMode(.inline)
        .task {
            do {
                let r = try await store.gitDiff(sessionId, path: file?.path)
                lines = r.text.components(separatedBy: "\n")
                truncated = r.truncated
            } catch {
                self.error = error.localizedDescription
            }
            loaded = true
        }
    }

    private func color(_ l: String) -> Color {
        if l.hasPrefix("+++") || l.hasPrefix("---") || l.hasPrefix("diff ") || l.hasPrefix("index ") { return .secondary }
        if l.hasPrefix("@@") { return Brand.sky }
        if l.hasPrefix("+") { return Brand.olive }
        if l.hasPrefix("-") { return Brand.rust }
        return Brand.ink
    }

    private func background(_ l: String) -> Color {
        if l.hasPrefix("+++") || l.hasPrefix("---") { return .clear }
        if l.hasPrefix("+") { return Brand.olive.opacity(0.10) }
        if l.hasPrefix("-") { return Brand.rust.opacity(0.10) }
        if l.hasPrefix("@@") { return Brand.sky.opacity(0.10) }
        return .clear
    }
}

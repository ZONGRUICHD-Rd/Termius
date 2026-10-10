import ConduitKit
import SwiftUI

/// Conversations started on the computer (Claude Desktop / Claude Code, Codex app / CLI) — pick one to continue
/// it from the phone, like remote control.
struct HistoryImportView: View {
    let agent: String
    let store: SessionStore
    var onOpen: (SessionSummary) -> Void

    @Environment(\.dismiss) private var dismiss
    @State private var items: [HistoryItem] = []
    @State private var supported = true
    @State private var loading = true
    @State private var error: String?
    @State private var importing: String?
    @State private var query = ""

    private var filtered: [HistoryItem] {
        query.isEmpty ? items : items.filter { $0.title.localizedCaseInsensitiveContains(query) || $0.cwd.localizedCaseInsensitiveContains(query) }
    }

    var body: some View {
        NavigationStack {
            List {
                if !supported {
                    Text("\(AgentStyle.of(agent).name) 暂不支持读取电脑上的历史对话。").foregroundStyle(.secondary)
                }
                if let error {
                    Label(error, systemImage: "exclamationmark.triangle.fill").foregroundStyle(Brand.rust)
                }
                ForEach(filtered) { item in
                    Button {
                        open(item)
                    } label: {
                        HStack(alignment: .top, spacing: 12) {
                            VStack(alignment: .leading, spacing: 4) {
                                Text(item.title).font(.body.weight(.semibold)).foregroundStyle(Brand.ink).lineLimit(2)
                                HStack(spacing: 6) {
                                    Label(NewTaskComposer.lastComponent(item.cwd), systemImage: "folder")
                                    Text(item.updatedAt.relative)
                                }
                                .font(.caption).foregroundStyle(.secondary).lineLimit(1)
                            }
                            Spacer()
                            if importing == item.id {
                                ProgressView()
                            } else if item.imported {
                                Text("已导入").font(.caption2.weight(.semibold)).foregroundStyle(Brand.olive)
                            }
                        }
                    }
                    .disabled(importing != nil)
                }
            }
            .overlay {
                if loading { ProgressView() }
                else if items.isEmpty && supported && error == nil {
                    ContentUnavailableView("没有找到对话", systemImage: "clock",
                                           description: Text("在电脑上用 \(AgentStyle.of(agent).name) 进行过的对话会显示在这里。"))
                }
            }
            .scrollContentBackground(.hidden)
            .paperBackground()
            .searchable(text: $query, prompt: "搜索")
            .navigationTitle("电脑上的对话")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button(role: .cancel) { dismiss() } label: { Image(systemName: "xmark") }
                }
            }
            .task { await load() }
            .refreshable { await load() }
        }
    }

    private func load() async {
        do {
            let r = try await store.history(agent: agent)
            supported = r.supported
            items = r.items
            error = nil
        } catch {
            self.error = error.localizedDescription
        }
        loading = false
    }

    private func open(_ item: HistoryItem) {
        importing = item.id
        Task {
            do {
                let s = try await store.importHistory(agent: agent, id: item.id)
                dismiss()
                onOpen(s)
            } catch {
                self.error = error.localizedDescription
            }
            importing = nil
        }
    }
}

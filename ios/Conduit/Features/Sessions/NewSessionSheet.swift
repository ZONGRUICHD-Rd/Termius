import ConduitKit
import SwiftUI

struct NewSessionSheet: View {
    let store: SessionStore
    let client: ConduitClient
    var onCreated: (SessionSummary) -> Void

    @Environment(\.dismiss) private var dismiss
    @AppStorage("lastAgent") private var agent = "claude"
    @AppStorage("lastFolder") private var folder = ""
    @State private var prompt = ""
    @State private var creating = false
    @State private var error: String?
    @State private var refreshing = false
    @FocusState private var promptFocused: Bool
    @Namespace private var agentNS

    private var agents: [AgentInfo] { client.hostInfo?.agents ?? [] }

    var body: some View {
        NavigationStack {
            ScrollView {
                VStack(alignment: .leading, spacing: 24) {
                    VStack(alignment: .leading, spacing: 10) {
                        HStack {
                            EyebrowLabel(text: "Agent")
                            Spacer()
                            Button {
                                Task {
                                    refreshing = true
                                    _ = await store.refreshAgents()
                                    refreshing = false
                                }
                            } label: {
                                Image(systemName: "arrow.clockwise").rotationEffect(.degrees(refreshing ? 360 : 0))
                                    .animation(refreshing ? .linear(duration: 1).repeatForever(autoreverses: false) : .default, value: refreshing)
                            }
                            .buttonStyle(.plain)
                            .foregroundStyle(.secondary)
                        }
                        GlassEffectContainer(spacing: 10) {
                            LazyVGrid(columns: [GridItem(.flexible(), spacing: 10), GridItem(.flexible(), spacing: 10)], spacing: 10) {
                                ForEach(agents) { a in agentCard(a) }
                            }
                        }
                        if agents.isEmpty {
                            Text("Waiting for the computer to report installed agents…").font(.footnote).foregroundStyle(.secondary)
                        }
                    }

                    VStack(alignment: .leading, spacing: 10) {
                        EyebrowLabel(text: "Project folder")
                        NavigationLink {
                            FolderPicker(store: store, selection: $folder)
                        } label: {
                            HStack(spacing: 12) {
                                Image(systemName: "folder.fill").foregroundStyle(Brand.clay)
                                VStack(alignment: .leading, spacing: 2) {
                                    Text(folder.isEmpty ? "Choose a folder" : (folder.split(whereSeparator: { $0 == "/" || $0 == "\\" }).last.map(String.init) ?? folder))
                                        .font(.body.weight(.semibold)).foregroundStyle(Brand.ink)
                                    if !folder.isEmpty {
                                        Text(folder).font(.caption.monospaced()).foregroundStyle(.secondary).lineLimit(1).truncationMode(.middle)
                                    }
                                }
                                Spacer()
                                Image(systemName: "chevron.right").font(.caption.weight(.bold)).foregroundStyle(.tertiary)
                            }
                            .padding(14)
                            .glassEffect(.regular.interactive(), in: .rect(cornerRadius: 20))
                        }
                        .buttonStyle(.plain)
                    }

                    VStack(alignment: .leading, spacing: 10) {
                        EyebrowLabel(text: "First message (optional)")
                        TextField("What should it work on?", text: $prompt, axis: .vertical)
                            .lineLimit(3...8)
                            .focused($promptFocused)
                            .padding(14)
                            .background(Brand.paperDeep, in: .rect(cornerRadius: 20))
                    }

                    if let error {
                        Label(error, systemImage: "exclamationmark.triangle.fill").font(.footnote).foregroundStyle(Brand.rust)
                    }
                }
                .padding(20)
            }
            .scrollDismissesKeyboard(.interactively)
            .paperBackground()
            .navigationTitle("New session")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button(role: .cancel) { dismiss() } label: { Image(systemName: "xmark") }
                }
                ToolbarItem(placement: .confirmationAction) {
                    Button {
                        create()
                    } label: {
                        if creating { ProgressView() } else { Text("Start").bold() }
                    }
                    .buttonStyle(.glassProminent)
                    .tint(Brand.clay)
                    .disabled(!canCreate)
                }
            }
            .onAppear {
                if !agents.contains(where: { $0.id == agent && $0.available }),
                   let first = agents.first(where: \.available) {
                    agent = first.id
                }
            }
        }
        .presentationDetents([.large])
    }

    private var canCreate: Bool {
        !creating && !folder.isEmpty && agents.contains { $0.id == agent && $0.available }
    }

    private func agentCard(_ a: AgentInfo) -> some View {
        let selected = a.id == agent
        return Button {
            withAnimation(.bouncy(duration: 0.35)) { agent = a.id }
        } label: {
            VStack(alignment: .leading, spacing: 8) {
                HStack {
                    AgentGlyph(agent: a.id, size: 20)
                    Spacer()
                    if selected {
                        Image(systemName: "checkmark.circle.fill").foregroundStyle(Brand.clay)
                            .transition(.scale.combined(with: .opacity))
                    }
                }
                Text(a.name).font(.headline).foregroundStyle(Brand.ink)
                Text(a.available ? (a.version ?? a.vendor) : "Not installed")
                    .font(.caption).foregroundStyle(.secondary).lineLimit(1)
            }
            .padding(14)
            .frame(maxWidth: .infinity, alignment: .leading)
            .opacity(a.available ? 1 : 0.45)
            .glassEffect(selected ? .regular.tint(Brand.clay.opacity(0.22)).interactive() : .regular.interactive(),
                         in: .rect(cornerRadius: 22))
        }
        .buttonStyle(.plain)
        .disabled(!a.available)
        .sensoryFeedback(.selection, trigger: agent)
    }

    private func create() {
        creating = true
        error = nil
        Task {
            do {
                let s = try await store.create(agent: agent, cwd: folder, prompt: prompt.trimmingCharacters(in: .whitespacesAndNewlines))
                dismiss()
                onCreated(s)
            } catch {
                self.error = error.localizedDescription
            }
            creating = false
        }
    }
}

/// Browses folders on the computer (directories only).
struct FolderPicker: View {
    let store: SessionStore
    @Binding var selection: String
    @State private var listing: DirectoryListing?
    @State private var path: String?
    @State private var error: String?
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        List {
            if let listing {
                if path == nil, !listing.recents.isEmpty {
                    Section {
                        ForEach(listing.recents, id: \.self) { r in
                            Button {
                                selection = r
                                dismiss()
                            } label: {
                                Label(r, systemImage: "clock.arrow.circlepath").lineLimit(1).truncationMode(.middle)
                            }
                        }
                    } header: { EyebrowLabel(text: "Recent") }
                }
                Section {
                    if let parent = listing.parent {
                        Button { open(parent) } label: { Label("..", systemImage: "arrow.turn.left.up") }
                    }
                    ForEach(listing.dirs) { d in
                        Button { open(d.path) } label: {
                            Label {
                                Text(d.name).foregroundStyle(Brand.ink)
                            } icon: {
                                Image(systemName: d.isRepo ? "shippingbox.fill" : "folder.fill")
                                    .foregroundStyle(d.isRepo ? Brand.clay : Brand.sand)
                            }
                        }
                    }
                } header: {
                    Text(listing.path).font(.caption.monospaced()).textCase(nil).lineLimit(1).truncationMode(.head)
                }
                if path == nil, listing.roots.count > 1 {
                    Section {
                        ForEach(listing.roots, id: \.self) { r in
                            Button { open(r) } label: { Label(r, systemImage: "internaldrive") }
                        }
                    } header: { EyebrowLabel(text: "Drives") }
                }
            } else if let error {
                Text(error).foregroundStyle(Brand.rust)
            } else {
                ProgressView()
            }
        }
        .scrollContentBackground(.hidden)
        .paperBackground()
        .navigationTitle(listing.map { $0.path.split(whereSeparator: { $0 == "/" || $0 == "\\" }).last.map(String.init) ?? $0.path } ?? "Folders")
        .navigationBarTitleDisplayMode(.inline)
        .toolbar {
            ToolbarItem(placement: .confirmationAction) {
                Button("Use") {
                    if let p = listing?.path {
                        selection = p
                        dismiss()
                    }
                }
                .buttonStyle(.glassProminent)
                .disabled(listing == nil)
            }
        }
        .task { await load(selection.isEmpty ? nil : selection) }
    }

    private func open(_ p: String) {
        path = p
        Task { await load(p) }
    }

    private func load(_ p: String?) async {
        do {
            let l = try await store.listDirectory(p)
            withAnimation(.smooth) { listing = l }
            error = nil
        } catch {
            if p != nil {
                await load(nil)
            } else {
                self.error = error.localizedDescription
            }
        }
    }
}

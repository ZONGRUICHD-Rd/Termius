import ConduitKit
import SwiftUI

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
                    } header: { EyebrowLabel(text: "最近使用") }
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
                    } header: { EyebrowLabel(text: "磁盘") }
                }
            } else if let error {
                Text(error).foregroundStyle(Brand.rust)
            } else {
                ProgressView()
            }
        }
        .scrollContentBackground(.hidden)
        .paperBackground()
        .navigationTitle(listing.map { $0.path.split(whereSeparator: { $0 == "/" || $0 == "\\" }).last.map(String.init) ?? $0.path } ?? "选择文件夹")
        .navigationBarTitleDisplayMode(.inline)
        .toolbar {
            ToolbarItem(placement: .confirmationAction) {
                Button("使用此文件夹") {
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

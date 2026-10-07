import SwiftUI

/// One transcript row. Equatable on (id, revision) so streaming only re-renders the row that changed.
struct ChatRow: View, Equatable {
    let item: ChatItem
    var agent: String
    var showReasoning: Bool

    static func == (a: ChatRow, b: ChatRow) -> Bool {
        a.item.id == b.item.id && a.item.revision == b.item.revision && a.item.decision == b.item.decision
            && a.showReasoning == b.showReasoning
    }

    var body: some View {
        switch item.kind {
        case .user: UserBubble(text: item.text)
        case .assistant: AssistantMessage(item: item, agent: agent)
        case .reasoning: if showReasoning { ReasoningRow(item: item) }
        case .tool: ToolRow(item: item)
        case .approval: ApprovalRow(item: item)
        case .error:
            Label(item.text, systemImage: "exclamationmark.octagon.fill")
                .font(.callout).foregroundStyle(Brand.rust)
                .padding(12).frame(maxWidth: .infinity, alignment: .leading)
                .background(Brand.rust.opacity(0.08), in: .rect(cornerRadius: 14))
        case .notice:
            Text(item.text).font(.caption).foregroundStyle(.secondary).frame(maxWidth: .infinity)
        }
    }
}

private struct UserBubble: View {
    let text: String

    var body: some View {
        HStack {
            Spacer(minLength: 48)
            Text(text)
                .font(.body)
                .foregroundStyle(Brand.ink)
                .padding(.horizontal, 14)
                .padding(.vertical, 10)
                .background(Brand.paperDeep, in: .rect(cornerRadius: 20, style: .continuous))
                .textSelection(.enabled)
        }
    }
}

private struct AssistantMessage: View {
    let item: ChatItem
    let agent: String

    var body: some View {
        HStack(alignment: .top, spacing: 10) {
            AgentGlyph(agent: agent, size: 16)
                .padding(.top, 3)
                .symbolEffect(.pulse, isActive: item.streaming)
            VStack(alignment: .leading, spacing: 0) {
                MarkdownText(text: item.text, streaming: item.streaming)
                if item.streaming {
                    StreamingCaret().padding(.top, 4)
                }
            }
            Spacer(minLength: 0)
        }
    }
}

private struct StreamingCaret: View {
    var body: some View {
        Capsule().fill(Brand.clay).frame(width: 14, height: 4)
            .phaseAnimator([0.3, 1.0]) { v, o in v.opacity(o) } animation: { _ in .easeInOut(duration: 0.5) }
    }
}

private struct ReasoningRow: View {
    let item: ChatItem
    @State private var open = false

    var body: some View {
        DisclosureGroup(isExpanded: $open) {
            Text(item.text).font(.footnote).foregroundStyle(.secondary).italic().frame(maxWidth: .infinity, alignment: .leading)
        } label: {
            Label(item.streaming ? "Thinking…" : "Thought", systemImage: "brain")
                .font(.footnote.weight(.medium)).foregroundStyle(.secondary)
        }
        .tint(.secondary)
    }
}

struct ToolRow: View {
    let item: ChatItem
    @State private var open = false

    private var color: Color {
        switch item.status {
        case "ok": return Brand.olive
        case "error": return Brand.rust
        default: return Brand.clay
        }
    }

    private var icon: String {
        let n = (item.name ?? "").lowercased()
        if n.contains("bash") || n.contains("shell") || n.contains("command") { return "terminal" }
        if n.contains("edit") || n.contains("write") || n.contains("patch") { return "pencil.line" }
        if n.contains("read") || n.contains("view") { return "doc.text" }
        if n.contains("grep") || n.contains("search") || n.contains("glob") || n.contains("find") { return "magnifyingglass" }
        if n.contains("web") || n.contains("fetch") { return "globe" }
        if n.contains("task") || n.contains("agent") { return "person.2" }
        return "wrench.and.screwdriver"
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            Button {
                withAnimation(.snappy) { open.toggle() }
            } label: {
                HStack(spacing: 10) {
                    ZStack {
                        if item.status == "running" {
                            RingProgress(progress: nil, color: color, lineWidth: 1.5)
                        }
                        Image(systemName: icon).font(.caption.weight(.semibold)).foregroundStyle(color)
                    }
                    .frame(width: 22, height: 22)
                    Text(item.title ?? item.name ?? "Tool")
                        .font(.callout.monospaced())
                        .foregroundStyle(Brand.ink)
                        .lineLimit(1)
                    Spacer()
                    if item.status == "error" {
                        Image(systemName: "xmark.circle.fill").foregroundStyle(Brand.rust).font(.caption)
                    } else if item.status == "ok" {
                        Image(systemName: "checkmark").foregroundStyle(Brand.olive).font(.caption.weight(.bold))
                    }
                    if item.output != nil || item.input != nil {
                        Image(systemName: "chevron.right")
                            .font(.caption2.weight(.bold)).foregroundStyle(.tertiary)
                            .rotationEffect(.degrees(open ? 90 : 0))
                    }
                }
                .contentShape(Rectangle())
            }
            .buttonStyle(.plain)

            if open {
                VStack(alignment: .leading, spacing: 8) {
                    if let input = item.input, !input.isEmpty {
                        Text(input).font(.caption.monospaced()).foregroundStyle(Brand.inkSoft).lineLimit(12)
                    }
                    if let output = item.output, !output.isEmpty {
                        ScrollView(.horizontal, showsIndicators: false) {
                            Text(output).font(.caption.monospaced()).foregroundStyle(Brand.ink).lineLimit(40)
                        }
                    }
                }
                .padding(10)
                .frame(maxWidth: .infinity, alignment: .leading)
                .background(Brand.paperDeep, in: .rect(cornerRadius: 12))
                .transition(.opacity.combined(with: .move(edge: .top)))
                .textSelection(.enabled)
            }
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 9)
        .overlay(RoundedRectangle(cornerRadius: 16).strokeBorder(Brand.hairline))
        .animation(.smooth, value: item.status)
    }
}

private struct ApprovalRow: View {
    let item: ChatItem

    var body: some View {
        HStack(spacing: 10) {
            Image(systemName: item.decision == nil ? "hand.raised.fill" : (item.decision == "deny" ? "hand.thumbsdown.fill" : "checkmark.shield.fill"))
                .foregroundStyle(item.decision == nil ? Brand.amber : (item.decision == "deny" ? Brand.rust : Brand.olive))
            VStack(alignment: .leading, spacing: 2) {
                Text(item.title ?? "Approval").font(.callout.weight(.semibold))
                Text(decisionLabel).font(.caption).foregroundStyle(.secondary)
            }
            Spacer()
        }
        .padding(12)
        .background((item.decision == nil ? Brand.amber : Brand.olive).opacity(0.08), in: .rect(cornerRadius: 14))
    }

    private var decisionLabel: String {
        switch item.decision {
        case nil: return "Waiting for you"
        case "allow": return "Allowed once"
        case "allow_always": return "Always allowed"
        case "deny": return "Denied"
        case "cancelled": return "Cancelled"
        default: return "Resolved"
        }
    }
}

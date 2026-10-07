import ConduitKit
import Foundation
import Observation

struct PlanItem: Hashable, Identifiable {
    var text: String
    var status: String // pending | in_progress | completed
    var id: String { text + status }
}

/// One row in the chat.
struct ChatItem: Identifiable, Equatable {
    enum Kind: String { case user, assistant, reasoning, tool, approval, error, notice }

    var id: String
    var kind: Kind
    var text: String = ""
    var streaming = false
    // tool
    var title: String?
    var name: String?
    var status: String?
    var input: String?
    var output: String?
    // approval
    var approvalKind: String?
    var canAlways = true
    var decision: String?
    var ts: Date = Date()
    /// Bumped on every mutation so Equatable rows re-render cheaply only when needed.
    var revision = 0
}

/// Client-side model of a session's event stream (docs/PROTOCOL.md §4). Idempotent: replays with seq ≤ lastSeq are ignored.
@MainActor
@Observable
final class Transcript {
    private(set) var items: [ChatItem] = []
    private(set) var plan: [PlanItem] = []
    private(set) var lastSeq: Int64 = 0
    var loaded = false
    @ObservationIgnored private var index: [String: Int] = [:]

    var pendingApprovals: [ChatItem] { items.filter { $0.kind == .approval && $0.decision == nil } }
    var isStreaming: Bool { items.last?.streaming == true }

    func reset() {
        items = []
        plan = []
        index = [:]
        lastSeq = 0
        loaded = false
    }

    func apply(_ events: [SessionEvent]) {
        guard !events.isEmpty else { return }
        var working = items
        var changedPlan: [PlanItem]?
        for e in events where e.seq > lastSeq {
            lastSeq = e.seq
            apply(e, to: &working, plan: &changedPlan)
        }
        items = working
        if let changedPlan { plan = changedPlan }
    }

    private func upsert(_ key: String, in items: inout [ChatItem], create: () -> ChatItem, update: (inout ChatItem) -> Void) {
        if let i = index[key], i < items.count, items[i].id == key {
            update(&items[i])
            items[i].revision += 1
        } else {
            var item = create()
            update(&item)
            index[key] = items.count
            items.append(item)
        }
    }

    private func apply(_ e: SessionEvent, to items: inout [ChatItem], plan: inout [PlanItem]?) {
        let ev = e.ev
        switch e.kind {
        case "user":
            items.append(ChatItem(id: "u\(e.seq)", kind: .user, text: ev["text"]?.string ?? "", ts: e.ts))
            index["u\(e.seq)"] = items.count - 1

        case "delta":
            let key = "m:" + (ev["mid"]?.string ?? "")
            let chunk = ev["text"]?.string ?? ""
            upsert(key, in: &items, create: { ChatItem(id: key, kind: .assistant, ts: e.ts) }) {
                $0.text += chunk
                $0.streaming = true
            }

        case "message":
            let key = "m:" + (ev["mid"]?.string ?? "")
            let text = ev["text"]?.string ?? ""
            upsert(key, in: &items, create: { ChatItem(id: key, kind: .assistant, ts: e.ts) }) {
                $0.text = text
                $0.streaming = false
            }

        case "reasoning":
            let key = "r:" + (ev["mid"]?.string ?? "")
            let text = ev["text"]?.string ?? ""
            let isDelta = ev["delta"]?.bool ?? false
            upsert(key, in: &items, create: { ChatItem(id: key, kind: .reasoning, ts: e.ts) }) {
                if isDelta { $0.text += text } else { $0.text = text }
                $0.streaming = isDelta
            }

        case "tool":
            let key = "t:" + (ev["tid"]?.string ?? "")
            upsert(key, in: &items, create: { ChatItem(id: key, kind: .tool, ts: e.ts) }) { item in
                if let v = ev["title"]?.string, !v.isEmpty { item.title = v }
                if let v = ev["name"]?.string { item.name = v }
                if let v = ev["status"]?.string { item.status = v }
                if let v = ev["input"]?.string { item.input = v }
                if let v = ev["output"]?.string { item.output = v }
            }

        case "approval":
            let key = "a:" + (ev["aid"]?.string ?? "")
            upsert(key, in: &items, create: { ChatItem(id: key, kind: .approval, ts: e.ts) }) {
                $0.title = ev["title"]?.string
                $0.text = ev["detail"]?.string ?? ""
                $0.approvalKind = ev["kind"]?.string
                $0.canAlways = ev["always"]?.bool ?? true
            }

        case "approval_done":
            let key = "a:" + (ev["aid"]?.string ?? "")
            if let i = index[key], i < items.count {
                items[i].decision = ev["decision"]?.string ?? "resolved"
                items[i].revision += 1
            }

        case "plan":
            plan = (ev["items"]?.array ?? []).map {
                PlanItem(text: $0["text"]?.string ?? "", status: $0["status"]?.string ?? "pending")
            }

        case "error":
            items.append(ChatItem(id: "e\(e.seq)", kind: .error, text: ev["text"]?.string ?? "", ts: e.ts))
            index["e\(e.seq)"] = items.count - 1

        case "notice":
            items.append(ChatItem(id: "n\(e.seq)", kind: .notice, text: ev["text"]?.string ?? "", ts: e.ts))
            index["n\(e.seq)"] = items.count - 1

        case "status":
            // A finished turn ends any dangling stream.
            if let state = ev["state"]?.string, state != "running", state != "starting", state != "waiting" {
                for i in items.indices where items[i].streaming {
                    items[i].streaming = false
                    items[i].revision += 1
                }
            }

        default:
            break
        }
    }
}

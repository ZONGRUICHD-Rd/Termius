import ConduitKit
import Foundation
import Observation

/// Sessions on the active computer: live summaries for the list / Island, transcripts for open chats.
@MainActor
@Observable
final class SessionStore {
    private(set) var summaries: [String: SessionSummary] = [:]
    private(set) var loadedOnce = false
    var lastError: String?

    @ObservationIgnored let client: ConduitClient
    @ObservationIgnored private var transcripts: [String: Transcript] = [:]
    @ObservationIgnored private var subscribed: Set<String> = []
    @ObservationIgnored var onSummariesChanged: (() -> Void)?

    init(client: ConduitClient) {
        self.client = client
    }

    var sorted: [SessionSummary] {
        summaries.values.sorted { a, b in
            if a.state.isActive != b.state.isActive { return a.state.isActive }
            return a.updatedAt > b.updatedAt
        }
    }

    var active: [SessionSummary] { sorted.filter { $0.state.isActive } }

    func sessions(for agent: String) -> [SessionSummary] { sorted.filter { $0.agent == agent } }

    func agentInfo(_ id: String) -> AgentInfo? { client.hostInfo?.agents.first { $0.id == id } }

    /// The session the Island / accessory should feature: blocked ones first, then the most recent active.
    var featured: SessionSummary? {
        let act = active
        return act.first { $0.state == .waiting } ?? act.first
    }

    func transcript(for id: String) -> Transcript {
        if let t = transcripts[id] { return t }
        let t = Transcript()
        transcripts[id] = t
        return t
    }

    // MARK: events

    func handle(event name: String, payload p: JSONValue) {
        switch name {
        case "session.event":
            guard let id = p["sessionId"]?.string, let e = SessionEvent(json: p) else { return }
            transcripts[id]?.apply([e])
        case "session.summary":
            guard let json = p["session"], let s = SessionSummary(json: json) else { return }
            summaries[s.id] = s
            onSummariesChanged?()
        case "session.removed":
            guard let id = p["sessionId"]?.string else { return }
            summaries[id] = nil
            transcripts[id] = nil
            subscribed.remove(id)
            onSummariesChanged?()
        default:
            break
        }
    }

    /// After every (re)connect: refresh the list and resume open transcripts from their last seq.
    func onConnected() async {
        await refresh()
        for id in subscribed { await subscribe(id) }
    }

    func refresh() async {
        do {
            let r = try await client.request("sessions.list")
            var next: [String: SessionSummary] = [:]
            for s in r["sessions"]?.array?.compactMap(SessionSummary.init(json:)) ?? [] { next[s.id] = s }
            summaries = next
            loadedOnce = true
            lastError = nil
            onSummariesChanged?()
        } catch {
            lastError = error.localizedDescription
        }
    }

    func subscribe(_ id: String) async {
        subscribed.insert(id)
        let t = transcript(for: id)
        do {
            let r = try await client.request("session.subscribe", ["sessionId": .string(id), "since": .int(t.lastSeq)])
            t.apply(r["events"]?.array?.compactMap(SessionEvent.init(json:)) ?? [])
            t.loaded = true
            if let json = r["session"], let s = SessionSummary(json: json) { summaries[s.id] = s }
        } catch {
            lastError = error.localizedDescription
        }
    }

    // MARK: actions

    func create(agent: String, cwd: String, prompt: String, mode: String? = nil, model: String? = nil,
                images: [Attachment] = []) async throws -> SessionSummary {
        var params: [String: JSONValue] = ["agent": .string(agent), "cwd": .string(cwd)]
        if !prompt.isEmpty { params["prompt"] = .string(prompt) }
        if let mode { params["mode"] = .string(mode) }
        if let model { params["model"] = .string(model) }
        if !images.isEmpty { params["images"] = .array(images.map(\.json)) }
        let r = try await client.request("session.create", params, timeout: 90)
        guard let json = r["session"], let s = SessionSummary(json: json) else { throw ClientError.notConnected }
        summaries[s.id] = s
        await subscribe(s.id)
        return s
    }

    func send(_ id: String, text: String, images: [Attachment] = []) async throws {
        var params: [String: JSONValue] = ["sessionId": .string(id), "text": .string(text)]
        if !images.isEmpty { params["images"] = .array(images.map(\.json)) }
        _ = try await client.request("session.send", params, timeout: 90)
    }

    func setOptions(_ id: String, model: String? = nil, mode: String? = nil) async throws {
        var params: [String: JSONValue] = ["sessionId": .string(id)]
        if let model { params["model"] = .string(model) }
        if let mode { params["mode"] = .string(mode) }
        let r = try await client.request("session.setOptions", params, timeout: 60)
        if let json = r["session"], let s = SessionSummary(json: json) { summaries[s.id] = s }
    }

    // MARK: code changes

    func gitStatus(_ id: String) async throws -> GitStatus {
        GitStatus(json: try await client.request("git.status", ["sessionId": .string(id)], timeout: 60))
    }

    func gitDiff(_ id: String, path: String?) async throws -> (text: String, truncated: Bool) {
        var params: [String: JSONValue] = ["sessionId": .string(id)]
        if let path { params["path"] = .string(path) }
        let r = try await client.request("git.diff", params, timeout: 60)
        return (r["diff"]?.string ?? "", r["truncated"]?.bool ?? false)
    }

    func gitCommit(_ id: String, message: String) async throws -> String {
        try await client.request("git.commit", ["sessionId": .string(id), "message": .string(message)], timeout: 60)["sha"]?.string ?? ""
    }

    func gitPush(_ id: String) async throws {
        _ = try await client.request("git.push", ["sessionId": .string(id)], timeout: 200)
    }

    func gitBranch(_ id: String, name: String) async throws {
        _ = try await client.request("git.branch", ["sessionId": .string(id), "name": .string(name)])
    }

    func gitPullRequest(_ id: String, title: String, body: String, draft: Bool) async throws -> String {
        try await client.request("git.pr", ["sessionId": .string(id), "title": .string(title), "body": .string(body),
                                            "draft": .bool(draft)], timeout: 240)["url"]?.string ?? ""
    }

    func gitDiscard(_ id: String, path: String) async throws {
        _ = try await client.request("git.discard", ["sessionId": .string(id), "path": .string(path)])
    }

    // MARK: conversations started on the PC

    func history(agent: String) async throws -> (supported: Bool, items: [HistoryItem]) {
        let r = try await client.request("history.list", ["agent": .string(agent)], timeout: 60)
        return (r["supported"]?.bool ?? false, r["items"]?.array?.compactMap(HistoryItem.init(json:)) ?? [])
    }

    func importHistory(agent: String, id: String) async throws -> SessionSummary {
        let r = try await client.request("history.import", ["agent": .string(agent), "id": .string(id)], timeout: 60)
        guard let json = r["session"], let s = SessionSummary(json: json) else { throw ClientError.notConnected }
        summaries[s.id] = s
        return s
    }

    // MARK: connection settings on the computer

    func setConnection(_ params: [String: JSONValue]) async throws {
        _ = try await client.request("host.setConnection", params, timeout: 30)
        await client.refreshHostInfo()
    }

    func interrupt(_ id: String) async {
        _ = try? await client.request("session.interrupt", ["sessionId": .string(id)])
    }

    func stop(_ id: String) async {
        _ = try? await client.request("session.stop", ["sessionId": .string(id)])
    }

    func delete(_ id: String) async {
        summaries[id] = nil
        _ = try? await client.request("session.delete", ["sessionId": .string(id)])
    }

    func respond(_ id: String, approvalId: String, decision: String) async throws {
        _ = try await client.request("approval.respond",
                                     ["sessionId": .string(id), "approvalId": .string(approvalId), "decision": .string(decision)])
    }

    func listDirectory(_ path: String?) async throws -> DirectoryListing {
        var params: [String: JSONValue] = [:]
        if let path { params["path"] = .string(path) }
        return DirectoryListing(json: try await client.request("fs.list", params))
    }

    func refreshAgents() async -> HostInfo? {
        guard let r = try? await client.request("agents.refresh", timeout: 30) else { return nil }
        return HostInfo(json: r)
    }
}

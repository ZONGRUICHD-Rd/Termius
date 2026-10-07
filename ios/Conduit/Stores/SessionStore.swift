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

    func create(agent: String, cwd: String, prompt: String) async throws -> SessionSummary {
        var params: [String: JSONValue] = ["agent": .string(agent), "cwd": .string(cwd)]
        if !prompt.isEmpty { params["prompt"] = .string(prompt) }
        let r = try await client.request("session.create", params, timeout: 60)
        guard let json = r["session"], let s = SessionSummary(json: json) else { throw ClientError.notConnected }
        summaries[s.id] = s
        await subscribe(s.id)
        return s
    }

    func send(_ id: String, text: String) async throws {
        _ = try await client.request("session.send", ["sessionId": .string(id), "text": .string(text)], timeout: 60)
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

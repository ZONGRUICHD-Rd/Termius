import Foundation

public enum SessionState: String, Codable, Sendable, CaseIterable {
    case starting, running, waiting, idle, error, stopped

    public var isActive: Bool { self == .starting || self == .running || self == .waiting }
}

public struct SessionSummary: Identifiable, Hashable, Sendable {
    public var id: String
    public var agent: String
    public var title: String
    public var cwd: String
    public var state: SessionState
    public var detail: String?
    public var done: Int
    public var total: Int
    public var turnStartedAt: Date?
    public var turnEndedAt: Date?
    public var createdAt: Date
    public var updatedAt: Date
    public var preview: String
    public var approvals: Int
    public var lastSeq: Int64

    public var progress: Double? { total > 0 ? Double(done) / Double(total) : nil }
    public var folderName: String {
        let parts = cwd.split(whereSeparator: { $0 == "/" || $0 == "\\" })
        return parts.last.map(String.init) ?? cwd
    }

    public init?(json: JSONValue) {
        guard let id = json["id"]?.string else { return nil }
        func date(_ key: String) -> Date? {
            guard let ms = json[key]?.double, ms > 0 else { return nil }
            return Date(timeIntervalSince1970: ms / 1000)
        }
        self.id = id
        agent = json["agent"]?.string ?? ""
        title = json["title"]?.string ?? "Session"
        cwd = json["cwd"]?.string ?? ""
        state = SessionState(rawValue: json["state"]?.string ?? "") ?? .stopped
        detail = json["detail"]?.string
        done = json["done"]?.int ?? 0
        total = json["total"]?.int ?? 0
        turnStartedAt = date("turnStartedAt")
        turnEndedAt = date("turnEndedAt")
        createdAt = date("createdAt") ?? Date()
        updatedAt = date("updatedAt") ?? Date()
        preview = json["preview"]?.string ?? ""
        approvals = json["approvals"]?.int ?? 0
        lastSeq = json["lastSeq"]?.int64 ?? 0
    }
}

public struct AgentInfo: Identifiable, Hashable, Sendable {
    public var id: String
    public var name: String
    public var vendor: String
    public var available: Bool
    public var version: String?
    public var detail: String

    public init?(json: JSONValue) {
        guard let id = json["id"]?.string else { return nil }
        self.id = id
        name = json["name"]?.string ?? id
        vendor = json["vendor"]?.string ?? ""
        available = json["available"]?.bool ?? false
        version = json["version"]?.string
        detail = json["detail"]?.string ?? ""
    }
}

public struct HostInfo: Hashable, Sendable {
    public var hostId: String
    public var hostName: String
    public var version: String
    public var os: String
    public var agents: [AgentInfo]
    public var endpoints: [String]
    public var relay: String?
    public var apns: Bool

    public init(json: JSONValue) {
        hostId = json["hostId"]?.string ?? ""
        hostName = json["hostName"]?.string ?? ""
        version = json["version"]?.string ?? ""
        os = json["os"]?.string ?? ""
        agents = json["agents"]?.array?.compactMap(AgentInfo.init(json:)) ?? []
        endpoints = json["endpoints"]?.array?.compactMap(\.string) ?? []
        relay = json["relay"]?.string
        apns = json["apns"]?.bool ?? false
    }
}

/// One entry of a session's event log.
public struct SessionEvent: Sendable {
    public var seq: Int64
    public var ts: Date
    public var ev: JSONValue
    public var kind: String { ev["k"]?.string ?? "" }

    public init?(json: JSONValue) {
        guard let seq = json["seq"]?.int64, let ev = json["ev"] else { return nil }
        self.seq = seq
        ts = Date(timeIntervalSince1970: (json["ts"]?.double ?? 0) / 1000)
        self.ev = ev
    }
}

public struct DirectoryListing: Sendable {
    public struct Entry: Hashable, Identifiable, Sendable {
        public var name: String
        public var path: String
        public var isRepo: Bool
        public var id: String { path }
    }

    public var path: String
    public var parent: String?
    public var home: String
    public var isRepo: Bool
    public var dirs: [Entry]
    public var roots: [String]
    public var recents: [String]

    public init(json: JSONValue) {
        path = json["path"]?.string ?? ""
        parent = json["parent"]?.string
        home = json["home"]?.string ?? ""
        isRepo = json["isRepo"]?.bool ?? false
        dirs = json["dirs"]?.array?.compactMap { d in
            d["path"]?.string.map { Entry(name: d["name"]?.string ?? $0, path: $0, isRepo: d["isRepo"]?.bool ?? false) }
        } ?? []
        roots = json["roots"]?.array?.compactMap(\.string) ?? []
        recents = json["recents"]?.array?.compactMap(\.string) ?? []
    }
}

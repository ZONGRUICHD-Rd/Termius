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
    public var model: String?
    public var mode: String?
    public var added: Int
    public var deleted: Int
    public var changedFiles: Int
    public var branch: String?
    public var isRepo: Bool

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
        title = json["title"]?.string ?? "对话"
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
        model = json["model"]?.string
        mode = json["mode"]?.string
        added = json["added"]?.int ?? 0
        deleted = json["deleted"]?.int ?? 0
        changedFiles = json["changedFiles"]?.int ?? 0
        branch = json["branch"]?.string
        isRepo = json["isRepo"]?.bool ?? false
    }
}

public struct AgentMode: Identifiable, Hashable, Sendable {
    public var id: String
    public var label: String
    public var description: String
}

public struct AgentInfo: Identifiable, Hashable, Sendable {
    public var id: String
    public var name: String
    public var vendor: String
    public var available: Bool
    public var version: String?
    public var detail: String
    public var modes: [AgentMode]
    public var models: [String]
    public var supportsHistory: Bool

    public init?(json: JSONValue) {
        guard let id = json["id"]?.string else { return nil }
        self.id = id
        name = json["name"]?.string ?? id
        vendor = json["vendor"]?.string ?? ""
        available = json["available"]?.bool ?? false
        version = json["version"]?.string
        detail = json["detail"]?.string ?? ""
        modes = json["modes"]?.array?.compactMap { m in
            m["id"]?.string.map { AgentMode(id: $0, label: m["label"]?.string ?? $0, description: m["description"]?.string ?? "") }
        } ?? []
        models = json["models"]?.array?.compactMap(\.string) ?? []
        supportsHistory = json["history"]?.bool ?? false
    }
}

public struct HostRoute: Hashable, Sendable, Identifiable {
    public var url: String
    public var kind: RouteKind
    public var id: String { url }
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
    public var routes: [HostRoute]
    public var lanEnabled: Bool
    public var cloudflareEnabled: Bool
    public var cloudflareMode: String
    public var cloudflareStatus: String
    public var cloudflareURL: String?
    public var ddnsEnabled: Bool
    public var ddnsDomain: String
    public var ddnsPort: Int
    public var ddnsTLS: Bool

    public init(json: JSONValue) {
        hostId = json["hostId"]?.string ?? ""
        hostName = json["hostName"]?.string ?? ""
        version = json["version"]?.string ?? ""
        os = json["os"]?.string ?? ""
        agents = json["agents"]?.array?.compactMap(AgentInfo.init(json:)) ?? []
        endpoints = json["endpoints"]?.array?.compactMap(\.string) ?? []
        relay = json["relay"]?.string
        apns = json["apns"]?.bool ?? false
        routes = json["routes"]?.array?.compactMap { r in
            r["url"]?.string.map { HostRoute(url: $0, kind: RouteKind(rawValue: r["kind"]?.string ?? "") ?? .custom) }
        } ?? []
        lanEnabled = json["lan"]?.bool ?? true
        cloudflareEnabled = json["cloudflare"]?["enabled"]?.bool ?? false
        cloudflareMode = json["cloudflare"]?["mode"]?.string ?? "quick"
        cloudflareStatus = json["cloudflare"]?["status"]?.string ?? "disabled"
        cloudflareURL = json["cloudflare"]?["url"]?.string
        ddnsEnabled = json["ddns"]?["enabled"]?.bool ?? false
        ddnsDomain = json["ddns"]?["domain"]?.string ?? ""
        ddnsPort = json["ddns"]?["port"]?.int ?? 47821
        ddnsTLS = json["ddns"]?["tls"]?.bool ?? false
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


/// git status for a session's project (Codex-style "review changes").
public struct GitStatus: Sendable {
    public struct File: Identifiable, Hashable, Sendable {
        public var path: String
        public var status: String   // added | modified | deleted | renamed
        public var added: Int
        public var deleted: Int
        public var id: String { path }
        public var name: String { path.split(separator: "/").last.map(String.init) ?? path }
        public var directory: String {
            let parts = path.split(separator: "/")
            return parts.count > 1 ? parts.dropLast().joined(separator: "/") : ""
        }
    }

    public var isRepo: Bool
    public var branch: String
    public var upstream: String
    public var ahead: Int
    public var behind: Int
    public var files: [File]
    public var added: Int
    public var deleted: Int
    public var hasGh: Bool

    public init(json: JSONValue) {
        isRepo = json["isRepo"]?.bool ?? false
        branch = json["branch"]?.string ?? ""
        upstream = json["upstream"]?.string ?? ""
        ahead = json["ahead"]?.int ?? 0
        behind = json["behind"]?.int ?? 0
        files = json["files"]?.array?.compactMap { f in
            f["path"]?.string.map {
                File(path: $0.replacingOccurrences(of: "\\", with: "/"), status: f["status"]?.string ?? "modified",
                     added: f["added"]?.int ?? 0, deleted: f["deleted"]?.int ?? 0)
            }
        } ?? []
        added = json["added"]?.int ?? 0
        deleted = json["deleted"]?.int ?? 0
        hasGh = json["hasGh"]?.bool ?? false
    }
}

/// A conversation started on the PC that can be continued from the phone.
public struct HistoryItem: Identifiable, Hashable, Sendable {
    public var id: String
    public var title: String
    public var cwd: String
    public var updatedAt: Date
    public var imported: Bool

    public init?(json: JSONValue) {
        guard let id = json["id"]?.string else { return nil }
        self.id = id
        title = json["title"]?.string ?? "对话"
        cwd = json["cwd"]?.string ?? ""
        updatedAt = Date(timeIntervalSince1970: (json["updatedAt"]?.double ?? 0) / 1000)
        imported = json["imported"]?.bool ?? false
    }
}

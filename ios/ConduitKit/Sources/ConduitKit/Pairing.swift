import Foundation

/// Contents of the QR code shown by Conduit Host: `conduit://pair?d=<base64url(json)>`.
public struct PairingPayload: Codable, Hashable, Sendable {
    public var version: Int
    public var hostId: String
    public var hostName: String
    /// Host static key-agreement public key (X9.63, 65 bytes).
    public var hostKey: Data
    public var token: String
    public var endpoints: [String]
    public var relay: String?

    public init(version: Int = 1, hostId: String, hostName: String, hostKey: Data, token: String, endpoints: [String], relay: String?) {
        self.version = version
        self.hostId = hostId
        self.hostName = hostName
        self.hostKey = hostKey
        self.token = token
        self.endpoints = endpoints
        self.relay = relay
    }

    public enum ParseError: Error, LocalizedError {
        case notAPairingCode
        case unsupportedVersion(Int)

        public var errorDescription: String? {
            switch self {
            case .notAPairingCode: return "This isn't a Conduit pairing code."
            case .unsupportedVersion(let v): return "Pairing code version \(v) isn't supported — update the app."
            }
        }
    }

    /// Accepts the full link, the raw `d` value, or text containing the link (e.g. pasted from a chat).
    public static func parse(_ text: String) throws -> PairingPayload {
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        var encoded = trimmed
        if let r = trimmed.range(of: "conduit://pair?d=") {
            encoded = String(trimmed[r.upperBound...].prefix { !$0.isWhitespace && $0 != "&" })
        } else if let comps = URLComponents(string: trimmed), let d = comps.queryItems?.first(where: { $0.name == "d" })?.value {
            encoded = d
        }
        guard let data = Data(base64URLEncoded: encoded), let json = try? JSONValue.decode(data),
              let id = json["id"]?.string, let k = json["k"]?.string, let key = Data(base64Encoded: k),
              let token = json["t"]?.string else {
            throw ParseError.notAPairingCode
        }
        let v = json["v"]?.int ?? 1
        guard v == 1 else { throw ParseError.unsupportedVersion(v) }
        return PairingPayload(version: v, hostId: id, hostName: json["n"]?.string ?? "Computer", hostKey: key, token: token,
                              endpoints: json["e"]?.array?.compactMap(\.string) ?? [], relay: json["r"]?.string)
    }
}

/// A computer this phone has paired with.
public struct PairedHost: Codable, Hashable, Identifiable, Sendable {
    public var id: String           // host id
    public var name: String
    public var hostKey: Data
    public var endpoints: [String]  // ws://ip:port
    public var relay: String?
    public var pairToken: String?   // only until the first successful connection
    public var pairedAt: Date
    public var lastRoute: String?

    public init(payload: PairingPayload) {
        id = payload.hostId
        name = payload.hostName
        hostKey = payload.hostKey
        endpoints = payload.endpoints
        relay = payload.relay
        pairToken = payload.token
        pairedAt = Date()
    }
}

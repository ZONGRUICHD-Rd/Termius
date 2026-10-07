import CryptoKit
import Foundation

public enum RouteKind: String, Sendable, Codable {
    case lan, tailscale, relay, custom

    public var label: String {
        switch self {
        case .lan: return "Local network"
        case .tailscale: return "Tailscale"
        case .relay: return "Relay"
        case .custom: return "Direct"
        }
    }

    public var isDirect: Bool { self != .relay }
}

public struct Endpoint: Hashable, Sendable {
    public var url: URL
    public var kind: RouteKind

    public static func candidates(for host: PairedHost) -> [Endpoint] {
        var list: [Endpoint] = []
        for e in host.endpoints {
            guard let base = URL(string: e) else { continue }
            let h = base.host ?? ""
            let kind: RouteKind
            if h.hasPrefix("100.") || h.hasSuffix(".ts.net") { kind = .tailscale }
            else if h.hasPrefix("192.168.") || h.hasPrefix("10.") || h.hasPrefix("172.") { kind = .lan }
            else { kind = .custom }
            list.append(Endpoint(url: base.appendingPathComponent("v1/connect"), kind: kind))
        }
        if let relay = host.relay, let base = URL(string: relay) {
            list.append(Endpoint(url: base.appendingPathComponent("v1/connect").appendingPathComponent(host.id), kind: .relay))
        }
        return list
    }
}

public enum HandshakeError: Error, LocalizedError {
    case rejected(reason: String, message: String)
    case protocolError(String)

    public var errorDescription: String? {
        switch self {
        case .rejected(_, let m): return m
        case .protocolError(let m): return "Handshake failed: \(m)"
        }
    }

    /// Errors that won't go away by retrying (re-pairing needed).
    public var isFatal: Bool {
        if case .rejected(let reason, _) = self { return reason == "unpaired" || reason == "wrong_host" || reason == "version" }
        return false
    }
}

/// An authenticated, end-to-end encrypted channel to the host over one transport.
public final class SecureConnection: @unchecked Sendable {
    public let transport: WebSocketTransport
    public let endpoint: Endpoint
    public let newlyPaired: Bool
    public let hostName: String
    private let cipher: FrameCipher

    private init(transport: WebSocketTransport, endpoint: Endpoint, cipher: FrameCipher, newlyPaired: Bool, hostName: String) {
        self.transport = transport
        self.endpoint = endpoint
        self.cipher = cipher
        self.newlyPaired = newlyPaired
        self.hostName = hostName
    }

    public static func open(endpoint: Endpoint, host: PairedHost, identity: DeviceIdentity, deviceName: String) async throws -> SecureConnection {
        let transport = WebSocketTransport(url: endpoint.url)
        do {
            try await transport.connect(timeout: endpoint.kind == .relay ? 10 : 6)
            return try await handshake(transport: transport, endpoint: endpoint, host: host, identity: identity, deviceName: deviceName)
        } catch {
            transport.close()
            throw error
        }
    }

    private static func handshake(transport: WebSocketTransport, endpoint: Endpoint, host: PairedHost,
                                  identity: DeviceIdentity, deviceName: String) async throws -> SecureConnection {
        let eph = P256.KeyAgreement.PrivateKey()
        let ephPub = eph.publicKey.x963Representation
        let nonceC = Data.random(16)
        var hello: [String: JSONValue] = [
            "type": "hello",
            "v": 1,
            "hostId": .string(host.id),
            "name": .string(deviceName),
            "clientStatic": .string(identity.publicKey.base64EncodedString()),
            "clientEph": .string(ephPub.base64EncodedString()),
            "nonce": .string(nonceC.base64EncodedString()),
        ]
        if let token = host.pairToken { hello["pairToken"] = .string(token) }
        try await transport.send(try JSONValue.object(hello).encoded(), text: true)

        // NWConnection receives aren't cancellable; a watchdog closes the socket instead, which fails the receive.
        let watchdog = Task {
            try await Task.sleep(nanoseconds: 10_000_000_000)
            transport.close()
        }
        defer { watchdog.cancel() }
        let (data, _) = try await transport.receive()
        let reply = try JSONValue.decode(data)
        switch reply["type"]?.string {
        case "welcome":
            break
        case "reject":
            throw HandshakeError.rejected(reason: reply["reason"]?.string ?? "", message: reply["message"]?.string ?? "Rejected by host")
        default:
            throw HandshakeError.protocolError("unexpected reply")
        }
        guard let hostEphB64 = reply["hostEph"]?.string, let hostEph = Data(base64Encoded: hostEphB64),
              let nonceB64 = reply["nonce"]?.string, let nonceH = Data(base64Encoded: nonceB64), nonceH.count == 16 else {
            throw HandshakeError.protocolError("malformed welcome")
        }
        let ee = try KeySchedule.agree(eph, hostEph)
        let es = try KeySchedule.agree(eph, host.hostKey)
        let ss = try KeySchedule.agree(identity.privateKey, host.hostKey)
        let keys = KeySchedule.derive(ee: ee, es: es, ss: ss, nonceClient: nonceC, nonceHost: nonceH, clientEph: ephPub, hostEph: hostEph)
        return SecureConnection(transport: transport, endpoint: endpoint,
                                cipher: FrameCipher(sendKey: keys.c2h, recvKey: keys.h2c),
                                newlyPaired: reply["paired"]?.bool ?? false,
                                hostName: reply["hostName"]?.string ?? host.name)
    }

    private let sendLock = NSLock()

    /// Seals and enqueues atomically so frame counters always hit the wire in order.
    public func send(_ message: JSONValue, completion: (@Sendable (Error?) -> Void)? = nil) throws {
        let plain = try message.encoded()
        sendLock.lock()
        defer { sendLock.unlock() }
        let frame = try cipher.seal(plain)
        transport.enqueue(frame, completion: completion)
    }

    /// Next decrypted message (runs off the main actor: decryption + JSON parsing).
    public func receive() async throws -> JSONValue {
        while true {
            let (data, isText) = try await transport.receive()
            if isText { continue }
            return try JSONValue.decode(try cipher.open(data))
        }
    }

    public func close() { transport.close() }
}

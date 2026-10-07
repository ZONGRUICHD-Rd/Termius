import CryptoKit
import Foundation

/// Key schedule shared with the Windows host (docs/PROTOCOL.md §2):
///
///     ee = ECDH(clientEph, hostEph)      es = ECDH(clientEph, hostStatic)      ss = ECDH(clientStatic, hostStatic)
///     okm = HKDF-SHA256(ikm: ee‖es‖ss, salt: nonceC‖nonceH, info: "conduit/v1"‖clientEph‖hostEph, 64)
///     c2h = okm[0..<32]   h2c = okm[32..<64]
public enum KeySchedule {
    public static let infoPrefix = Data("conduit/v1".utf8)

    public static func derive(ee: Data, es: Data, ss: Data,
                              nonceClient: Data, nonceHost: Data,
                              clientEph: Data, hostEph: Data) -> (c2h: SymmetricKey, h2c: SymmetricKey) {
        let ikm = SymmetricKey(data: ee + es + ss)
        let okm = HKDF<SHA256>.deriveKey(inputKeyMaterial: ikm,
                                         salt: nonceClient + nonceHost,
                                         info: infoPrefix + clientEph + hostEph,
                                         outputByteCount: 64)
        let bytes = okm.withUnsafeBytes { Data($0) }
        return (SymmetricKey(data: bytes.prefix(32)), SymmetricKey(data: bytes.suffix(32)))
    }

    public static func agree(_ priv: P256.KeyAgreement.PrivateKey, _ peerX963: Data) throws -> Data {
        let peer = try P256.KeyAgreement.PublicKey(x963Representation: peerX963)
        let secret = try priv.sharedSecretFromKeyAgreement(with: peer)
        return secret.withUnsafeBytes { Data($0) }
    }
}

public enum CryptoError: Error, LocalizedError {
    case frameTooShort
    case outOfOrder(expected: UInt64, got: UInt64)

    public var errorDescription: String? {
        switch self {
        case .frameTooShort: return "Encrypted frame too short"
        case .outOfOrder(let e, let g): return "Out-of-order frame (expected \(e), got \(g))"
        }
    }
}

/// AES-256-GCM framing: counter(8, big endian) ‖ ciphertext ‖ tag(16); nonce = 0x00000000 ‖ counter.
public final class FrameCipher: @unchecked Sendable {
    private let sendKey: SymmetricKey
    private let recvKey: SymmetricKey
    private var sendCounter: UInt64 = 0
    private var recvCounter: UInt64 = 0
    private let lock = NSLock()

    public init(sendKey: SymmetricKey, recvKey: SymmetricKey) {
        self.sendKey = sendKey
        self.recvKey = recvKey
    }

    private static func nonce(_ counter: UInt64) -> AES.GCM.Nonce {
        var n = Data(count: 4)
        withUnsafeBytes(of: counter.bigEndian) { n.append(contentsOf: $0) }
        return try! AES.GCM.Nonce(data: n)
    }

    public func seal(_ plaintext: Data) throws -> Data {
        lock.lock()
        defer { lock.unlock() }
        let counter = sendCounter
        let box = try AES.GCM.seal(plaintext, using: sendKey, nonce: Self.nonce(counter))
        sendCounter += 1
        var frame = Data(capacity: 8 + plaintext.count + 16)
        withUnsafeBytes(of: counter.bigEndian) { frame.append(contentsOf: $0) }
        frame.append(box.ciphertext)
        frame.append(box.tag)
        return frame
    }

    public func open(_ frame: Data) throws -> Data {
        guard frame.count >= 24 else { throw CryptoError.frameTooShort }
        lock.lock()
        defer { lock.unlock() }
        let bytes = [UInt8](frame)
        var counter: UInt64 = 0
        for b in bytes[0..<8] { counter = (counter << 8) | UInt64(b) }
        guard counter == recvCounter else { throw CryptoError.outOfOrder(expected: recvCounter, got: counter) }
        let ct = Data(bytes[8..<(bytes.count - 16)])
        let tag = Data(bytes[(bytes.count - 16)...])
        let box = try AES.GCM.SealedBox(nonce: Self.nonce(counter), ciphertext: ct, tag: tag)
        let plain = try AES.GCM.open(box, using: recvKey)
        recvCounter += 1
        return plain
    }
}

/// The phone's long-lived identity. Its public key is what the host remembers when pairing.
public struct DeviceIdentity: Sendable {
    public let privateKey: P256.KeyAgreement.PrivateKey

    public init() { privateKey = P256.KeyAgreement.PrivateKey() }

    public init(rawRepresentation: Data) throws {
        privateKey = try P256.KeyAgreement.PrivateKey(rawRepresentation: rawRepresentation)
    }

    public var rawRepresentation: Data { privateKey.rawRepresentation }
    public var publicKey: Data { privateKey.publicKey.x963Representation }

    /// Same id the host computes: base64url(sha256(pub))[..22].
    public var deviceId: String { String(Data(SHA256.hash(data: publicKey)).base64URLEncoded().prefix(22)) }
}

public extension Data {
    func base64URLEncoded() -> String {
        base64EncodedString().replacingOccurrences(of: "+", with: "-").replacingOccurrences(of: "/", with: "_")
            .replacingOccurrences(of: "=", with: "")
    }

    init?(base64URLEncoded s: String) {
        var t = s.replacingOccurrences(of: "-", with: "+").replacingOccurrences(of: "_", with: "/")
        while t.count % 4 != 0 { t += "=" }
        self.init(base64Encoded: t)
    }

    static func random(_ count: Int) -> Data {
        var d = Data(count: count)
        _ = d.withUnsafeMutableBytes { SecRandomCopyBytes(kSecRandomDefault, count, $0.baseAddress!) }
        return d
    }

    var hex: String { map { String(format: "%02x", $0) }.joined() }
}

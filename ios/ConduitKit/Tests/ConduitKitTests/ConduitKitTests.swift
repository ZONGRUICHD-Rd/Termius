import CryptoKit
import XCTest
@testable import ConduitKit

final class CryptoVectorTests: XCTestCase {
    private func seq(_ start: UInt8, _ count: Int = 32) -> Data {
        Data((0..<count).map { UInt8(truncatingIfNeeded: Int(start) + $0) })
    }

    /// Must match windows/tests/Conduit.Host.Core.Tests/CryptoTests.cs (and an independent Python check).
    func testKeyScheduleMatchesHostVector() throws {
        let clientStatic = try P256.KeyAgreement.PrivateKey(rawRepresentation: seq(0x01))
        let clientEph = try P256.KeyAgreement.PrivateKey(rawRepresentation: seq(0x21))
        let hostStatic = try P256.KeyAgreement.PrivateKey(rawRepresentation: seq(0x41))
        let hostEph = try P256.KeyAgreement.PrivateKey(rawRepresentation: seq(0x61))
        let cEph = clientEph.publicKey.x963Representation
        let hEph = hostEph.publicKey.x963Representation

        let ee = try KeySchedule.agree(clientEph, hEph)
        let es = try KeySchedule.agree(clientEph, hostStatic.publicKey.x963Representation)
        let ss = try KeySchedule.agree(clientStatic, hostStatic.publicKey.x963Representation)
        let keys = KeySchedule.derive(ee: ee, es: es, ss: ss, nonceClient: seq(0xA0, 16), nonceHost: seq(0xB0, 16),
                                      clientEph: cEph, hostEph: hEph)

        XCTAssertEqual(keys.c2h.withUnsafeBytes { Data($0) }.hex, "3f51d364288afb9d4d6b7a37f3ccb04ed619c1fe4ed9a7234d2209ecc59b1ad4")
        XCTAssertEqual(keys.h2c.withUnsafeBytes { Data($0) }.hex, "5acb2e6085e9810c12be0b27444985756a5558f87e941cf44b459be0338fa20c")

        let cipher = FrameCipher(sendKey: keys.c2h, recvKey: keys.h2c)
        let frame = try cipher.seal(Data(#"{"t":"req","id":1,"m":"ping"}"#.utf8))
        XCTAssertEqual(frame.hex, "00000000000000002910cbbb7c6a058a31e9432db6b46e5b32642f5b2439ac1c2c812ce00aa94c894b8e09e74cf10d86fe34d4c3a2")
    }

    func testFrameRoundTripRejectsReplay() throws {
        let k1 = SymmetricKey(size: .bits256), k2 = SymmetricKey(size: .bits256)
        let a = FrameCipher(sendKey: k1, recvKey: k2)
        let b = FrameCipher(sendKey: k2, recvKey: k1)
        let f = try a.seal(Data("hello".utf8))
        XCTAssertEqual(String(data: try b.open(f), encoding: .utf8), "hello")
        XCTAssertThrowsError(try b.open(f))
    }

    func testDeviceIdMatchesHostAlgorithm() throws {
        let id = try DeviceIdentity(rawRepresentation: seq(0x01))
        XCTAssertEqual(id.publicKey.hex,
                       "04515c3d6eb9e396b904d3feca7f54fdcd0cc1e997bf375dca515ad0a6c3b4035f4536be3a50f318fbf9a5475902a221502bef0d57e08c53b2cc0a56f17d9f9354")
        XCTAssertEqual(id.deviceId.count, 22)
    }
}

final class PairingTests: XCTestCase {
    func testParsesLinkAndBuildsCandidates() throws {
        let key = P256.KeyAgreement.PrivateKey().publicKey.x963Representation
        let json: JSONValue = [
            "v": 1, "id": "abc", "n": "DESKTOP", "k": .string(key.base64EncodedString()), "t": "tok",
            "e": ["ws://192.168.1.5:47821", "ws://100.101.102.103:47821"], "r": "wss://relay.example.com",
        ]
        let link = "conduit://pair?d=" + (try json.encoded()).base64URLEncoded()
        let p = try PairingPayload.parse("Scan this: \(link) ")
        XCTAssertEqual(p.hostId, "abc")
        XCTAssertEqual(p.hostKey, key)
        XCTAssertEqual(p.endpoints.count, 2)

        let host = PairedHost(payload: p)
        let c = Endpoint.candidates(for: host)
        XCTAssertEqual(c.map(\.kind), [.lan, .tailscale, .relay])
        XCTAssertEqual(c[0].url.absoluteString, "ws://192.168.1.5:47821/v1/connect")
        XCTAssertEqual(c[2].url.absoluteString, "wss://relay.example.com/v1/connect/abc")
    }

    func testRejectsGarbage() {
        XCTAssertThrowsError(try PairingPayload.parse("hello"))
    }

    func testJSONNumbersRoundTripAsIntegers() throws {
        let v: JSONValue = ["since": 42]
        XCTAssertEqual(String(data: try v.encoded(), encoding: .utf8), #"{"since":42}"#)
        XCTAssertEqual(try JSONValue.decode(Data(#"{"a":1.5,"b":true,"c":7}"#.utf8))["c"]?.int, 7)
    }
}

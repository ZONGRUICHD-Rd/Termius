import ConduitKit
import Foundation

/// Persists paired computers and the phone's identity key.
enum HostStore {
    private static var url: URL { AppFiles.directory.appendingPathComponent("hosts.json") }

    static func load() -> [PairedHost] {
        guard let data = try? Data(contentsOf: url) else { return [] }
        return (try? JSONDecoder().decode([PairedHost].self, from: data)) ?? []
    }

    static func save(_ hosts: [PairedHost]) {
        guard let data = try? JSONEncoder().encode(hosts) else { return }
        try? data.write(to: url, options: [.atomic, .completeFileProtectionUntilFirstUserAuthentication])
    }

    static func loadIdentity() -> DeviceIdentity {
        if let raw = SecureStore.read("device-key"), let id = try? DeviceIdentity(rawRepresentation: raw) { return id }
        let id = DeviceIdentity()
        SecureStore.write("device-key", id.rawRepresentation)
        return id
    }
}

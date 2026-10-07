import Foundation
import Security

/// Small secret storage: Keychain first, falling back to a protected file when the Keychain is unavailable
/// (e.g. missing keychain-access-groups entitlement after re-signing, or inside LiveContainer).
enum SecureStore {
    private static let service = "dev.conduit.secure"

    static func read(_ key: String) -> Data? {
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: key,
            kSecReturnData as String: true,
            kSecMatchLimit as String: kSecMatchLimitOne,
        ]
        var out: AnyObject?
        if SecItemCopyMatching(query as CFDictionary, &out) == errSecSuccess, let data = out as? Data { return data }
        return try? Data(contentsOf: fileURL(key))
    }

    static func write(_ key: String, _ data: Data) {
        let base: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: key,
        ]
        SecItemDelete(base as CFDictionary)
        var add = base
        add[kSecValueData as String] = data
        add[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly
        if SecItemAdd(add as CFDictionary, nil) == errSecSuccess {
            try? FileManager.default.removeItem(at: fileURL(key))
            return
        }
        try? data.write(to: fileURL(key), options: [.atomic, .completeFileProtectionUntilFirstUserAuthentication])
    }

    static func delete(_ key: String) {
        let base: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: key,
        ]
        SecItemDelete(base as CFDictionary)
        try? FileManager.default.removeItem(at: fileURL(key))
    }

    private static func fileURL(_ key: String) -> URL {
        let dir = AppFiles.directory.appendingPathComponent("secure", isDirectory: true)
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        return dir.appendingPathComponent(key)
    }
}

enum AppFiles {
    static let directory: URL = {
        let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first!
        let dir = base.appendingPathComponent("Conduit", isDirectory: true)
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        return dir
    }()
}

import ActivityKit
import Foundation
import UIKit

/// Detects where the app is running and which system features are actually usable.
///
/// LiveContainer runs this app as a "guest" inside its own process: there is no app extension (so no Live Activity
/// UI, even though ActivityKit calls may succeed), App Groups and push entitlements belong to LiveContainer, and the
/// guest's Info.plist ATS keys aren't applied. Conduit avoids depending on any of those and falls back to the
/// in-app Island for agent progress.
enum RuntimeEnvironment {
    static let isLiveContainer: Bool = {
        let env = ProcessInfo.processInfo.environment
        if env.keys.contains(where: { $0.hasPrefix("LC_") || $0 == "LIVECONTAINER" }) { return true }
        let bundlePath = Bundle.main.bundlePath
        if bundlePath.contains("/Documents/Applications/") || bundlePath.localizedCaseInsensitiveContains("LiveContainer") { return true }
        if let exec = Bundle.main.executablePath, exec.localizedCaseInsensitiveContains("LiveContainer") { return true }
        for cls in ["LCSharedUtils", "LCUtils", "LCAppInfo"] where NSClassFromString(cls) != nil { return true }
        return false
    }()

    /// The widget extension that renders the Live Activity is present (stripped in the LiveContainer IPA).
    static let hasWidgetExtension: Bool = {
        guard let plugins = Bundle.main.builtInPlugInsURL,
              let items = try? FileManager.default.contentsOfDirectory(atPath: plugins.path) else { return false }
        return items.contains { $0.hasSuffix(".appex") }
    }()

    static var liveActivitiesSupported: Bool {
        !isLiveContainer && hasWidgetExtension && ActivityAuthorizationInfo().areActivitiesEnabled
    }

    static var deviceName: String {
        let name = UIDevice.current.name
        return name.isEmpty || name == "iPhone" ? "iPhone (\(UIDevice.current.model))" : name
    }

    static var appVersion: String {
        (Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String) ?? "1.0"
    }

    static var bundleId: String { Bundle.main.bundleIdentifier ?? "dev.conduit.app" }
}

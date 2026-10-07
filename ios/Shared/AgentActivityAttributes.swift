import ActivityKit
import Foundation

/// Live Activity shown in the Dynamic Island and on the Lock Screen while agents run.
/// `ContentState` is also produced by the Windows host for APNs pushes
/// (ApnsLiveActivityPusher.BuildContentState) — keep the field names and types in sync.
struct AgentActivityAttributes: ActivityAttributes {
    struct ContentState: Codable, Hashable {
        var sessionId: String
        var title: String
        var agent: String
        /// starting | running | waiting | idle | error | stopped
        var state: String
        var detail: String
        var done: Int
        var total: Int
        /// Unix seconds when the current turn started (0 = unknown).
        var startedAt: Double
        /// Number of sessions currently active on the computer.
        var running: Int
        /// Number of sessions blocked on an approval.
        var waiting: Int
        var updatedAt: Double

        var progress: Double? { total > 0 ? min(1, Double(done) / Double(total)) : nil }
        var isActive: Bool { state == "running" || state == "starting" || state == "waiting" }
        var isWaiting: Bool { state == "waiting" }
        var startDate: Date { startedAt > 0 ? Date(timeIntervalSince1970: startedAt) : Date() }
    }

    var hostName: String
}

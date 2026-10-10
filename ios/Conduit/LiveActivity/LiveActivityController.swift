import ActivityKit
import ConduitKit
import Foundation
import UIKit

/// Drives the Dynamic Island / Lock Screen Live Activity from session summaries.
///
/// One activity per computer, featuring the most relevant session (blocked > most recent), plus counts.
/// While the app runs it updates the activity directly; when suspended, the Windows host can keep it fresh
/// over APNs (Settings → iPhone push on the host) using the push token we register here.
@MainActor
final class LiveActivityController {
    static let enabledKey = "liveActivitiesEnabled"

    private var activity: Activity<AgentActivityAttributes>?
    private var lastState: AgentActivityAttributes.ContentState?
    private var lastUpdate = Date.distantPast
    private var pendingUpdate: Task<Void, Never>?
    private var tokenTask: Task<Void, Never>?
    var onPushToken: ((String) -> Void)?

    var isEnabled: Bool {
        UserDefaults.standard.object(forKey: Self.enabledKey) as? Bool ?? true
    }

    var isSupported: Bool { RuntimeEnvironment.liveActivitiesSupported }

    init() {
        // Adopt an activity that survived an app relaunch.
        activity = Activity<AgentActivityAttributes>.activities.first
        if let activity { observeToken(activity) }
    }

    func update(summaries: [SessionSummary], hostName: String, pushCapable: Bool) {
        guard isSupported, isEnabled else {
            endNow()
            return
        }
        let active = summaries.filter { $0.state.isActive }
        guard let primary = active.first(where: { $0.state == .waiting }) ?? active.max(by: { $0.updatedAt < $1.updatedAt })
                ?? (activity != nil ? summaries.max(by: { $0.updatedAt < $1.updatedAt }) : nil) else { return }

        let state = AgentActivityAttributes.ContentState(
            sessionId: primary.id,
            title: primary.title,
            agent: primary.agent,
            state: primary.state.rawValue,
            detail: primary.detail ?? "",
            done: primary.done,
            total: primary.total,
            startedAt: primary.turnStartedAt?.timeIntervalSince1970 ?? 0,
            running: active.count,
            waiting: active.filter { $0.state == .waiting }.count,
            updatedAt: Date().timeIntervalSince1970)

        if active.isEmpty {
            finish(with: state)
            return
        }
        if activity == nil {
            start(state: state, hostName: hostName, pushCapable: pushCapable)
            return
        }
        schedule(state)
    }

    private func start(state: AgentActivityAttributes.ContentState, hostName: String, pushCapable: Bool) {
        let content = ActivityContent(state: state, staleDate: Date().addingTimeInterval(15 * 60), relevanceScore: 100)
        do {
            activity = try Activity.request(attributes: AgentActivityAttributes(hostName: hostName), content: content,
                                            pushType: pushCapable ? .token : nil)
        } catch {
            // No push entitlement (sideloaded / re-signed builds): fall back to app-driven updates.
            activity = try? Activity.request(attributes: AgentActivityAttributes(hostName: hostName), content: content, pushType: nil)
        }
        lastState = state
        lastUpdate = Date()
        if let activity { observeToken(activity) }
    }

    private func observeToken(_ activity: Activity<AgentActivityAttributes>) {
        tokenTask?.cancel()
        tokenTask = Task { [weak self] in
            for await token in activity.pushTokenUpdates {
                let hex = token.map { String(format: "%02x", $0) }.joined()
                self?.onPushToken?(hex)
            }
        }
    }

    /// Coalesces bursts (e.g. a tool storm) into ≤ 2 updates per second; state changes go out immediately.
    private func schedule(_ state: AgentActivityAttributes.ContentState) {
        guard state.withoutTimestamp != lastState?.withoutTimestamp else { return }
        let urgent = state.state != lastState?.state || state.sessionId != lastState?.sessionId
        pendingUpdate?.cancel()
        let wait = urgent ? 0 : max(0, 0.5 - Date().timeIntervalSince(lastUpdate))
        pendingUpdate = Task { [weak self] in
            if wait > 0 { try? await Task.sleep(nanoseconds: UInt64(wait * 1_000_000_000)) }
            guard !Task.isCancelled, let self, let activity = self.activity else { return }
            self.lastState = state
            self.lastUpdate = Date()
            let alert: AlertConfiguration? = (urgent && state.isWaiting)
                ? AlertConfiguration(title: "需要你的确认",
                                     body: LocalizedStringResource(stringLiteral: "\(state.title): \(state.detail)"),
                                     sound: .default)
                : nil
            await activity.update(ActivityContent(state: state, staleDate: Date().addingTimeInterval(15 * 60), relevanceScore: state.isWaiting ? 100 : 50),
                                  alertConfiguration: alert)
        }
    }

    private func finish(with state: AgentActivityAttributes.ContentState) {
        guard let activity else { return }
        pendingUpdate?.cancel()
        self.activity = nil
        lastState = nil
        Task {
            await activity.end(ActivityContent(state: state, staleDate: nil), dismissalPolicy: .after(Date().addingTimeInterval(4 * 60)))
        }
    }

    func endNow() {
        pendingUpdate?.cancel()
        let all = Activity<AgentActivityAttributes>.activities
        activity = nil
        lastState = nil
        Task {
            for a in all { await a.end(nil, dismissalPolicy: .immediate) }
        }
    }
}

private extension AgentActivityAttributes.ContentState {
    var withoutTimestamp: AgentActivityAttributes.ContentState {
        var c = self
        c.updatedAt = 0
        return c
    }
}

import ConduitKit
import Foundation
import Observation
import SwiftUI
import UIKit

/// App-wide state: paired computers, the live connection to the selected one, and the Live Activity.
@MainActor
@Observable
final class AppModel {
    private(set) var hosts: [PairedHost]
    private(set) var client: ConduitClient?
    private(set) var store: SessionStore?
    var activeHostId: String? {
        didSet { UserDefaults.standard.set(activeHostId, forKey: "activeHostId") }
    }
    /// Set by deep links (conduit://session/<id>, Live Activity taps); consumed by the UI.
    var pendingSessionId: String?
    var selectedTab: AppTab = .sessions
    var showPairing = false

    @ObservationIgnored let identity: DeviceIdentity
    @ObservationIgnored let liveActivity = LiveActivityController()
    @ObservationIgnored private var backgroundTask: UIBackgroundTaskIdentifier = .invalid

    init() {
        identity = HostStore.loadIdentity()
        hosts = HostStore.load()
        let saved = UserDefaults.standard.string(forKey: "activeHostId")
        activeHostId = hosts.contains { $0.id == saved } ? saved : hosts.first?.id
        liveActivity.onPushToken = { [weak self] token in self?.registerPush(token) }
        if let id = activeHostId { connect(to: id) }
    }

    var activeHost: PairedHost? { hosts.first { $0.id == activeHostId } }

    // MARK: connection

    func connect(to hostId: String) {
        guard let host = hosts.first(where: { $0.id == hostId }) else { return }
        client?.stop()
        activeHostId = hostId
        let client = ConduitClient(host: host, identity: identity, deviceName: RuntimeEnvironment.deviceName)
        let store = SessionStore(client: client)
        client.onEvent = { [weak store] name, payload in store?.handle(event: name, payload: payload) }
        client.onConnected = { [weak store] in await store?.onConnected() }
        client.onHostUpdated = { [weak self] updated in self?.saveHost(updated) }
        store.onSummariesChanged = { [weak self] in self?.summariesChanged() }
        self.client = client
        self.store = store
        client.start()
    }

    private func summariesChanged() {
        guard let store, let client else { return }
        liveActivity.update(summaries: Array(store.summaries.values), hostName: client.host.name,
                            pushCapable: client.hostInfo?.apns == true)
    }

    private func registerPush(_ token: String) {
        guard let client else { return }
        #if DEBUG
        let defaultEnv = "sandbox"
        #else
        let defaultEnv = "production"
        #endif
        let env = UserDefaults.standard.string(forKey: "apnsEnvironment") ?? defaultEnv
        Task {
            _ = try? await client.request("push.register", [
                "token": .string(token), "env": .string(env), "bundleId": .string(RuntimeEnvironment.bundleId),
            ])
        }
    }

    // MARK: pairing

    enum PairingError: LocalizedError {
        case timeout
        var errorDescription: String? { "Couldn't reach the computer. Make sure Conduit Host is running and on the same network, Tailscale or relay." }
    }

    /// Adds the host from a QR payload and waits until the encrypted connection is established.
    func pair(with payload: PairingPayload) async throws {
        var host = PairedHost(payload: payload)
        if let existing = hosts.first(where: { $0.id == host.id }) { host.pairedAt = existing.pairedAt }
        saveHost(host)
        connect(to: host.id)
        let deadline = Date().addingTimeInterval(25)
        while Date() < deadline {
            switch client?.status {
            case .connected:
                selectedTab = .sessions
                return
            case .failed(let message):
                removeHost(host.id)
                throw NSError(domain: "Conduit", code: 1, userInfo: [NSLocalizedDescriptionKey: message])
            default:
                try await Task.sleep(nanoseconds: 150_000_000)
            }
        }
        throw PairingError.timeout
    }

    func saveHost(_ host: PairedHost) {
        if let i = hosts.firstIndex(where: { $0.id == host.id }) { hosts[i] = host } else { hosts.append(host) }
        HostStore.save(hosts)
        if client?.host.id == host.id { client?.updateHost(host) }
    }

    func removeHost(_ id: String) {
        hosts.removeAll { $0.id == id }
        HostStore.save(hosts)
        if activeHostId == id {
            client?.stop()
            client = nil
            store = nil
            liveActivity.endNow()
            activeHostId = hosts.first?.id
            if let next = activeHostId { connect(to: next) }
        }
    }

    // MARK: app lifecycle

    func scenePhaseChanged(_ phase: ScenePhase) {
        switch phase {
        case .active:
            endBackgroundTask()
            client?.resume()
        case .background:
            // Keep the socket (and the Live Activity) alive a little longer for quick app switches.
            backgroundTask = UIApplication.shared.beginBackgroundTask(withName: "conduit.keepalive") { [weak self] in
                self?.endBackgroundTask()
            }
        default:
            break
        }
    }

    private func endBackgroundTask() {
        guard backgroundTask != .invalid else { return }
        UIApplication.shared.endBackgroundTask(backgroundTask)
        backgroundTask = .invalid
    }

    func handle(url: URL) {
        guard url.scheme == "conduit" else { return }
        switch url.host {
        case "pair":
            if let payload = try? PairingPayload.parse(url.absoluteString) {
                Task { try? await pair(with: payload) }
            }
        case "session":
            let id = url.lastPathComponent
            if !id.isEmpty {
                selectedTab = .sessions
                pendingSessionId = id
            }
        default:
            break
        }
    }
}

enum AppTab: Hashable {
    case sessions, computers, settings
}

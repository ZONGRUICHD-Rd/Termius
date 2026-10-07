import Foundation
import Network
import Observation

public enum ConnectionStatus: Equatable, Sendable {
    case idle
    case connecting
    case connected(RouteKind)
    case reconnecting(attempt: Int, lastError: String?)
    case failed(String)

    public var isConnected: Bool {
        if case .connected = self { return true }
        return false
    }
}

public struct ServerError: Error, LocalizedError {
    public var message: String
    public var code: String
    public var errorDescription: String? { message }
}

public enum ClientError: Error, LocalizedError {
    case notConnected
    case noEndpoints

    public var errorDescription: String? {
        switch self {
        case .notConnected: return "Not connected to your computer"
        case .noEndpoints: return "No way to reach this computer — add a relay or Tailscale address on the host"
        }
    }
}

/// Keeps one live, encrypted connection to a paired computer.
///
/// "Smooth" by design:
///  * Happy-Eyeballs racing of every known route (LAN, Tailscale, custom, relay); direct routes get a head start.
///  * While on the relay, keeps probing direct routes and migrates seamlessly when one becomes reachable.
///  * Reconnects immediately on network changes (NWPathMonitor) and with short backoff otherwise.
///  * App-level heartbeat measures RTT and detects dead sockets within seconds.
/// Callers resubscribe in `onConnected` with their last seen seq, so nothing is lost across reconnects.
@MainActor
@Observable
public final class ConduitClient {
    public private(set) var host: PairedHost
    public private(set) var status: ConnectionStatus = .idle
    public private(set) var rttMs: Int?
    public private(set) var hostInfo: HostInfo?

    @ObservationIgnored public var deviceName: String
    @ObservationIgnored public var onEvent: ((String, JSONValue) -> Void)?
    @ObservationIgnored public var onConnected: (() async -> Void)?
    @ObservationIgnored public var onHostUpdated: ((PairedHost) -> Void)?

    @ObservationIgnored private let identity: DeviceIdentity
    @ObservationIgnored private var connection: SecureConnection?
    @ObservationIgnored private var pending: [Int64: CheckedContinuation<JSONValue, Error>] = [:]
    @ObservationIgnored private var nextId: Int64 = 0
    @ObservationIgnored private var mainTask: Task<Void, Never>?
    @ObservationIgnored private var receiveTask: Task<Void, Never>?
    @ObservationIgnored private var heartbeatTask: Task<Void, Never>?
    @ObservationIgnored private var upgradeTask: Task<Void, Never>?
    @ObservationIgnored private var disconnected: CheckedContinuation<Void, Never>?
    @ObservationIgnored private var wakeContinuation: CheckedContinuation<Void, Never>?
    @ObservationIgnored private var connectWaiters: [CheckedContinuation<Void, Never>] = []
    @ObservationIgnored private let pathMonitor = NWPathMonitor()
    @ObservationIgnored private var lastPathSignature = ""

    public init(host: PairedHost, identity: DeviceIdentity, deviceName: String) {
        self.host = host
        self.identity = identity
        self.deviceName = deviceName
        pathMonitor.pathUpdateHandler = { [weak self] path in
            let sig = path.status == .satisfied
                ? path.availableInterfaces.map { "\($0.type)\($0.index)" }.joined(separator: ",")
                : "offline"
            Task { @MainActor in self?.pathChanged(signature: sig, satisfied: path.status == .satisfied) }
        }
        pathMonitor.start(queue: DispatchQueue(label: "conduit.path"))
    }

    deinit {
        pathMonitor.cancel()
    }

    // MARK: lifecycle

    public func start() {
        guard mainTask == nil else { return }
        mainTask = Task { [weak self] in await self?.run() }
    }

    public func stop() {
        mainTask?.cancel()
        mainTask = nil
        tearDown(error: ClientError.notConnected)
        let d = disconnected
        disconnected = nil
        d?.resume()
        fireWake()
        status = .idle
    }

    /// Call when the app returns to the foreground: verifies the socket or reconnects right away.
    public func resume() {
        switch status {
        case .connected:
            Task { await probe(timeout: 2.5) }
        case .failed, .idle:
            stop()
            start()
        default:
            fireWake()
        }
    }

    public func updateHost(_ newHost: PairedHost) {
        host = newHost
    }

    private func run() async {
        var attempt = 0
        var lastError: String?
        while !Task.isCancelled {
            status = attempt == 0 ? .connecting : .reconnecting(attempt: attempt, lastError: lastError)
            do {
                let conn = try await race(Endpoint.candidates(for: host), preferRelay: host.lastRoute == RouteKind.relay.rawValue)
                if Task.isCancelled {
                    conn.close()
                    return
                }
                attempt = 0
                lastError = nil
                await attach(conn)
                await withCheckedContinuation { disconnected = $0 }
                // Reconnect immediately after a drop.
            } catch let e as HandshakeError where e.isFatal {
                status = .failed(e.localizedDescription)
                await sleepOrWake(3600)
                attempt = 0
            } catch {
                if Task.isCancelled { return }
                attempt += 1
                lastError = error.localizedDescription
                status = .reconnecting(attempt: attempt, lastError: lastError)
                // 0.25s, 0.5s, 1s, 2s, then every 4s — cheap because failed candidates fail fast.
                await sleepOrWake(min(4, 0.25 * pow(2, Double(min(attempt - 1, 4)))))
            }
        }
    }

    // MARK: racing

    private func race(_ candidates: [Endpoint], preferRelay: Bool) async throws -> SecureConnection {
        guard !candidates.isEmpty else { throw ClientError.noEndpoints }
        let host = self.host, identity = self.identity, name = self.deviceName
        let hasDirect = candidates.contains { $0.kind.isDirect }
        return try await withThrowingTaskGroup(of: SecureConnection.self) { group in
            for c in candidates {
                let delay: UInt64 = (c.kind == .relay && hasDirect && !preferRelay) ? 350_000_000 : 0
                group.addTask {
                    if delay > 0 { try await Task.sleep(nanoseconds: delay) }
                    return try await SecureConnection.open(endpoint: c, host: host, identity: identity, deviceName: name)
                }
            }
            var winner: SecureConnection?
            var best: Error?
            while let result = await group.nextResult() {
                switch result {
                case .success(let conn):
                    if winner == nil {
                        winner = conn
                        group.cancelAll()
                    } else {
                        conn.close()
                    }
                case .failure(let error):
                    // A host-level rejection is more informative than "unreachable".
                    if best == nil || error is HandshakeError { best = error }
                }
            }
            if let winner { return winner }
            throw best ?? ClientError.noEndpoints
        }
    }

    private func attach(_ conn: SecureConnection) async {
        let old = connection
        connection = conn
        status = .connected(conn.endpoint.kind)
        receiveTask = Task { [weak self] in await self?.receiveLoop(conn) }
        old?.close()

        let waiters = connectWaiters
        connectWaiters = []
        waiters.forEach { $0.resume() }

        var updated = host
        updated.lastRoute = conn.endpoint.kind.rawValue
        if conn.newlyPaired || updated.pairToken != nil { updated.pairToken = nil }
        if updated != host {
            host = updated
            onHostUpdated?(updated)
        }

        startHeartbeat(conn)
        startUpgradeProbe(conn)

        if let info = try? await request("host.info") {
            let hi = HostInfo(json: info)
            hostInfo = hi
            var h = host
            if !hi.endpoints.isEmpty { h.endpoints = hi.endpoints }
            h.relay = hi.relay ?? h.relay
            if !hi.hostName.isEmpty { h.name = hi.hostName }
            if h != host {
                host = h
                onHostUpdated?(h)
            }
        }
        await onConnected?()
    }

    private func receiveLoop(_ conn: SecureConnection) async {
        do {
            while true {
                let msg = try await conn.receive()
                guard conn === connection else { return }
                handle(msg)
            }
        } catch {
            guard conn === connection else { return }
            tearDown(error: error)
            let d = disconnected
            disconnected = nil
            d?.resume()
        }
    }

    private func tearDown(error: Error) {
        heartbeatTask?.cancel()
        upgradeTask?.cancel()
        receiveTask?.cancel()
        connection?.close()
        connection = nil
        rttMs = nil
        let p = pending
        pending = [:]
        p.values.forEach { $0.resume(throwing: error) }
    }

    private func handle(_ msg: JSONValue) {
        switch msg["t"]?.string {
        case "res":
            guard let id = msg["id"]?.int64, let cont = pending.removeValue(forKey: id) else { return }
            if msg["ok"]?.bool == true {
                cont.resume(returning: msg["r"] ?? .null)
            } else {
                cont.resume(throwing: ServerError(message: msg["err"]?.string ?? "Request failed", code: msg["code"]?.string ?? "error"))
            }
        case "evt":
            if let name = msg["e"]?.string { onEvent?(name, msg["p"] ?? .null) }
        default:
            break
        }
    }

    // MARK: requests

    public func request(_ method: String, _ params: [String: JSONValue] = [:], timeout: Double = 20) async throws -> JSONValue {
        if connection == nil { await waitForConnection(timeout: 8) }
        guard let conn = connection else { throw ClientError.notConnected }
        nextId += 1
        let id = nextId
        let msg: JSONValue = ["t": "req", "id": .int(id), "m": .string(method), "p": .object(params)]
        return try await withCheckedThrowingContinuation { cont in
            pending[id] = cont
            do {
                try conn.send(msg) { [weak self] error in
                    guard let error else { return }
                    Task { @MainActor in self?.fail(id, error) }
                }
            } catch {
                fail(id, error)
                return
            }
            Task { [weak self] in
                try? await Task.sleep(nanoseconds: UInt64(timeout * 1_000_000_000))
                self?.fail(id, TransportError.timeout)
            }
        }
    }

    private func fail(_ id: Int64, _ error: Error) {
        pending.removeValue(forKey: id)?.resume(throwing: error)
    }

    private func waitForConnection(timeout: Double) async {
        guard connection == nil else { return }
        await withCheckedContinuation { (cont: CheckedContinuation<Void, Never>) in
            connectWaiters.append(cont)
            Task { [weak self] in
                try? await Task.sleep(nanoseconds: UInt64(timeout * 1_000_000_000))
                guard let self else { return }
                if let i = self.connectWaiters.firstIndex(where: { _ in true }) {
                    // Resume the oldest waiter; any others are resumed by their own timers or on connect.
                    self.connectWaiters.remove(at: i).resume()
                }
            }
        }
    }

    // MARK: heartbeat, migration, network changes

    private func startHeartbeat(_ conn: SecureConnection) {
        heartbeatTask?.cancel()
        heartbeatTask = Task { [weak self] in
            while !Task.isCancelled {
                guard let self, conn === self.connection else { return }
                await self.probe(timeout: 6)
                try? await Task.sleep(nanoseconds: 15_000_000_000)
            }
        }
    }

    private func probe(timeout: Double) async {
        guard let conn = connection else { return }
        let start = Date()
        do {
            _ = try await request("ping", timeout: timeout)
            if conn === connection { rttMs = Int(Date().timeIntervalSince(start) * 1000) }
        } catch {
            if conn === connection { conn.close() } // receive loop notices and reconnects
        }
    }

    /// On the relay, keep trying direct routes and migrate without the user noticing.
    private func startUpgradeProbe(_ conn: SecureConnection) {
        upgradeTask?.cancel()
        guard conn.endpoint.kind == .relay else { return }
        let direct = Endpoint.candidates(for: host).filter { $0.kind.isDirect }
        guard !direct.isEmpty else { return }
        upgradeTask = Task { [weak self] in
            var delay: UInt64 = 5_000_000_000
            while !Task.isCancelled {
                try? await Task.sleep(nanoseconds: delay)
                delay = min(delay * 2, 60_000_000_000)
                guard let self, conn === self.connection, !Task.isCancelled else { return }
                if let better = try? await self.race(direct, preferRelay: false) {
                    guard conn === self.connection else {
                        better.close()
                        return
                    }
                    let inFlight = self.pending
                    self.pending = [:]
                    inFlight.values.forEach { $0.resume(throwing: ClientError.notConnected) }
                    self.heartbeatTask?.cancel()
                    await self.attach(better)
                    return
                }
            }
        }
    }

    private func pathChanged(signature: String, satisfied: Bool) {
        defer { lastPathSignature = signature }
        guard signature != lastPathSignature, !lastPathSignature.isEmpty else { return }
        guard satisfied else { return }
        if status.isConnected {
            // Interface changed (Wi-Fi ↔ cellular): make sure the socket survived, else reconnect now.
            Task { await probe(timeout: 2.5) }
        } else {
            fireWake()
        }
    }

    private func sleepOrWake(_ seconds: Double) async {
        await withCheckedContinuation { (cont: CheckedContinuation<Void, Never>) in
            wakeContinuation = cont
            Task { [weak self] in
                try? await Task.sleep(nanoseconds: UInt64(seconds * 1_000_000_000))
                self?.fireWake()
            }
        }
    }

    private func fireWake() {
        let c = wakeContinuation
        wakeContinuation = nil
        c?.resume()
    }
}

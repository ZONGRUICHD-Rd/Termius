import Foundation
import Network

public enum TransportError: Error, LocalizedError {
    case closed(String?)
    case unreachable(String)
    case timeout

    public var errorDescription: String? {
        switch self {
        case .closed(let r): return r.map { "Connection closed: \($0)" } ?? "Connection closed"
        case .unreachable(let r): return "Unreachable: \(r)"
        case .timeout: return "Timed out"
        }
    }
}

/// WebSocket over Network.framework.
///
/// Why not URLSessionWebSocketTask: Network.framework isn't subject to App Transport Security (plain ws:// to a
/// Tailscale 100.x address works without Info.plist exceptions — important under LiveContainer, where the guest's
/// Info.plist ATS keys are not honoured), lets us enable TCP_NODELAY and fails fast on unreachable paths, which
/// makes racing several endpoints cheap.
public final class WebSocketTransport: @unchecked Sendable {
    public let url: URL
    private let connection: NWConnection
    private let queue = DispatchQueue(label: "conduit.ws", qos: .userInitiated)

    public init(url: URL) {
        self.url = url
        let tcp = NWProtocolTCP.Options()
        tcp.noDelay = true
        tcp.enableKeepalive = true
        tcp.keepaliveIdle = 15
        tcp.keepaliveInterval = 5
        tcp.keepaliveCount = 3
        tcp.connectionTimeout = 7
        let params: NWParameters
        if url.scheme == "wss" {
            params = NWParameters(tls: NWProtocolTLS.Options(), tcp: tcp)
        } else {
            params = NWParameters(tls: nil, tcp: tcp)
        }
        params.serviceClass = .responsiveData
        let ws = NWProtocolWebSocket.Options()
        ws.autoReplyPing = true
        ws.maximumMessageSize = 16 * 1024 * 1024
        ws.setAdditionalHeaders([("User-Agent", "Conduit-iOS/1")])
        params.defaultProtocolStack.applicationProtocols.insert(ws, at: 0)
        connection = NWConnection(to: .url(url), using: params)
    }

    /// Path the connection is using (Wi-Fi / cellular), once ready.
    public var interfaceType: NWInterface.InterfaceType? {
        connection.currentPath?.availableInterfaces.first.map(\.type)
    }

    public func connect(timeout: TimeInterval = 8) async throws {
        let once = Once()
        try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { (cont: CheckedContinuation<Void, Error>) in
                connection.stateUpdateHandler = { [weak self] state in
                    switch state {
                    case .ready:
                        once.run { cont.resume() }
                    case .failed(let e):
                        once.run { cont.resume(throwing: TransportError.unreachable(e.localizedDescription)) }
                    case .waiting(let e):
                        // No route right now (e.g. LAN address while on cellular): fail fast so another candidate wins.
                        once.run { cont.resume(throwing: TransportError.unreachable(e.localizedDescription)) }
                        self?.connection.cancel()
                    case .cancelled:
                        once.run { cont.resume(throwing: CancellationError()) }
                    default:
                        break
                    }
                }
                connection.start(queue: queue)
                queue.asyncAfter(deadline: .now() + timeout) { [weak self] in
                    once.run {
                        cont.resume(throwing: TransportError.timeout)
                        self?.connection.cancel()
                    }
                }
            }
        } onCancel: {
            connection.cancel()
        }
    }

    /// Enqueues a frame immediately (non-blocking). Frames are sent in call order.
    public func enqueue(_ data: Data, text: Bool = false, completion: (@Sendable (Error?) -> Void)? = nil) {
        let meta = NWProtocolWebSocket.Metadata(opcode: text ? .text : .binary)
        let context = NWConnection.ContentContext(identifier: "frame", metadata: [meta])
        connection.send(content: data, contentContext: context, isComplete: true, completion: .contentProcessed { error in
            completion?(error)
        })
    }

    public func send(_ data: Data, text: Bool = false) async throws {
        let meta = NWProtocolWebSocket.Metadata(opcode: text ? .text : .binary)
        let context = NWConnection.ContentContext(identifier: "frame", metadata: [meta])
        try await withCheckedThrowingContinuation { (cont: CheckedContinuation<Void, Error>) in
            connection.send(content: data, contentContext: context, isComplete: true, completion: .contentProcessed { error in
                if let error { cont.resume(throwing: TransportError.closed(error.localizedDescription)) } else { cont.resume() }
            })
        }
    }

    /// Next complete message. Throws when the socket closes.
    public func receive() async throws -> (data: Data, isText: Bool) {
        try await withCheckedThrowingContinuation { cont in
            connection.receiveMessage { content, context, _, error in
                if let error {
                    cont.resume(throwing: TransportError.closed(error.localizedDescription))
                    return
                }
                let meta = context?.protocolMetadata(definition: NWProtocolWebSocket.definition) as? NWProtocolWebSocket.Metadata
                switch meta?.opcode {
                case .close:
                    var reason: String?
                    if let closeCode = meta?.closeCode {
                        switch closeCode {
                        case .applicationCode(let code): reason = Self.describe(code: code)
                        case .privateCode(let code): reason = "code \(code)"
                        case .protocolCode(let code): reason = "\(code)"
                        @unknown default: break
                        }
                    }
                    cont.resume(throwing: TransportError.closed(reason))
                case .text:
                    cont.resume(returning: (content ?? Data(), true))
                default:
                    if let content {
                        cont.resume(returning: (content, false))
                    } else {
                        cont.resume(throwing: TransportError.closed(nil))
                    }
                }
            }
        }
    }

    private static func describe(code: UInt16) -> String {
        switch code {
        case 4404: return "Computer is offline (not connected to the relay)"
        case 4408: return "Computer didn't answer through the relay"
        case 4429: return "Relay busy"
        default: return "code \(code)"
        }
    }

    public func close() {
        let meta = NWProtocolWebSocket.Metadata(opcode: .close)
        meta.closeCode = .protocolCode(.normalClosure)
        let ctx = NWConnection.ContentContext(identifier: "close", metadata: [meta])
        connection.send(content: nil, contentContext: ctx, isComplete: true, completion: .contentProcessed { [connection] _ in
            connection.cancel()
        })
        queue.asyncAfter(deadline: .now() + 1) { [connection] in connection.cancel() }
    }
}

/// Runs a closure at most once (thread-safe).
final class Once: @unchecked Sendable {
    private var done = false
    private let lock = NSLock()

    func run(_ body: () -> Void) {
        lock.lock()
        let first = !done
        done = true
        lock.unlock()
        if first { body() }
    }
}

import ConduitKit
import SwiftUI

// MARK: typography

extension Font {
    /// Editorial serif (New York) for titles — warm, bookish, calm.
    static func serif(_ style: Font.TextStyle, weight: Font.Weight = .regular) -> Font {
        .system(style, design: .serif, weight: weight)
    }
}

// MARK: background

/// Ivory paper with a whisper of warmth at the top. Content sits on this; Liquid Glass floats above it.
struct PaperBackground: View {
    var body: some View {
        ZStack {
            Brand.paper
            RadialGradient(colors: [Brand.clay.opacity(0.10), .clear], center: .topTrailing, startRadius: 10, endRadius: 520)
            RadialGradient(colors: [Brand.sand.opacity(0.08), .clear], center: .bottomLeading, startRadius: 10, endRadius: 480)
        }
        .ignoresSafeArea()
    }
}

extension View {
    func paperBackground() -> some View {
        background { PaperBackground() }
    }
}

// MARK: status

struct StatePill: View {
    let state: SessionState
    var approvals = 0

    var body: some View {
        HStack(spacing: 5) {
            Circle()
                .fill(Brand.stateColor(state.rawValue))
                .frame(width: 7, height: 7)
                .phaseAnimator([false, true]) { dot, on in
                    dot.opacity(state == .running && on ? 0.35 : 1)
                } animation: { _ in .easeInOut(duration: 0.8) }
            Text(approvals > 0 ? "\(approvals) to approve" : Brand.stateLabel(state.rawValue))
                .font(.caption.weight(.semibold))
                .foregroundStyle(Brand.stateColor(state.rawValue))
        }
        .padding(.horizontal, 9)
        .padding(.vertical, 4)
        .background(Brand.stateColor(state.rawValue).opacity(0.12), in: Capsule())
    }
}

struct RingProgress: View {
    var progress: Double?
    var color: Color
    var lineWidth: CGFloat = 3
    @State private var spin = false

    var body: some View {
        ZStack {
            Circle().stroke(color.opacity(0.2), lineWidth: lineWidth)
            if let progress {
                Circle().trim(from: 0, to: max(0.02, progress))
                    .stroke(color, style: StrokeStyle(lineWidth: lineWidth, lineCap: .round))
                    .rotationEffect(.degrees(-90))
                    .animation(.smooth, value: progress)
            } else {
                Circle().trim(from: 0, to: 0.28)
                    .stroke(color, style: StrokeStyle(lineWidth: lineWidth, lineCap: .round))
                    .rotationEffect(.degrees(spin ? 360 : 0))
                    .animation(.linear(duration: 1.1).repeatForever(autoreverses: false), value: spin)
                    .onAppear { spin = true }
            }
        }
    }
}

/// Agent mark on a soft tinted disc.
struct AgentAvatar: View {
    let agent: String
    var size: CGFloat = 36
    var state: SessionState?

    var body: some View {
        let tint = AgentStyle.of(agent).tint
        ZStack {
            Circle().fill(tint.opacity(0.14))
            AgentGlyph(agent: agent, size: size * 0.5)
            if let state, state.isActive {
                RingProgress(progress: nil, color: Brand.stateColor(state.rawValue), lineWidth: 2)
                    .padding(-2)
            }
        }
        .frame(width: size, height: size)
    }
}

struct ConnectionBadge: View {
    let status: ConnectionStatus
    var rtt: Int?

    var body: some View {
        HStack(spacing: 6) {
            Image(systemName: icon)
                .font(.caption.weight(.bold))
                .foregroundStyle(color)
                .symbolEffect(.pulse, isActive: isWorking)
            Text(label).font(.caption.weight(.semibold))
            if let rtt, status.isConnected {
                Text("\(rtt) ms").font(.caption.monospacedDigit()).foregroundStyle(.secondary)
            }
        }
    }

    private var isWorking: Bool {
        switch status {
        case .connecting, .reconnecting: return true
        default: return false
        }
    }

    private var icon: String {
        switch status {
        case .connected(let r):
            switch r {
            case .lan: return "wifi"
            case .tailscale: return "point.3.connected.trianglepath.dotted"
            case .relay: return "cloud.fill"
            case .custom: return "network"
            }
        case .failed: return "exclamationmark.triangle.fill"
        case .idle: return "pause.circle"
        default: return "arrow.triangle.2.circlepath"
        }
    }

    private var color: Color {
        switch status {
        case .connected: return Brand.olive
        case .failed: return Brand.rust
        default: return Brand.amber
        }
    }

    private var label: String {
        switch status {
        case .connected(let r): return r.label
        case .connecting: return "Connecting…"
        case .reconnecting(let n, _): return n > 2 ? "Reconnecting…" : "Connecting…"
        case .failed: return "Can't connect"
        case .idle: return "Offline"
        }
    }
}

/// "3m 12s" style elapsed time that ticks.
struct ElapsedText: View {
    let since: Date

    var body: some View {
        TimelineView(.periodic(from: .now, by: 1)) { ctx in
            Text(Self.format(ctx.date.timeIntervalSince(since)))
                .monospacedDigit()
        }
    }

    static func format(_ t: TimeInterval) -> String {
        let s = max(0, Int(t))
        if s < 60 { return "\(s)s" }
        if s < 3600 { return "\(s / 60)m \(s % 60)s" }
        return "\(s / 3600)h \((s % 3600) / 60)m"
    }
}

extension Date {
    var relative: String {
        let f = RelativeDateTimeFormatter()
        f.unitsStyle = .short
        return f.localizedString(for: self, relativeTo: .now)
    }
}

/// Section header in the editorial style.
struct EyebrowLabel: View {
    let text: String

    var body: some View {
        Text(text.uppercased())
            .font(.caption2.weight(.semibold))
            .tracking(1.2)
            .foregroundStyle(Brand.inkSoft)
    }
}

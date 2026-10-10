import ActivityKit
import SwiftUI
import WidgetKit

@main
struct ConduitWidgetsBundle: WidgetBundle {
    var body: some Widget {
        AgentLiveActivity()
    }
}

/// Dynamic Island + Lock Screen presentation of the running agent(s).
struct AgentLiveActivity: Widget {
    var body: some WidgetConfiguration {
        ActivityConfiguration(for: AgentActivityAttributes.self) { context in
            LockScreenView(state: context.state, hostName: context.attributes.hostName)
                .activityBackgroundTint(Brand.paper.opacity(0.35))
                .activitySystemActionForegroundColor(Brand.clay)
                .widgetURL(URL(string: "conduit://session/\(context.state.sessionId)"))
        } dynamicIsland: { context in
            let s = context.state
            return DynamicIsland {
                DynamicIslandExpandedRegion(.leading) {
                    HStack(spacing: 8) {
                        AgentGlyph(agent: s.agent, size: 22, color: Brand.stateColor(s.state))
                            .symbolEffect(.pulse, isActive: s.isActive)
                        VStack(alignment: .leading, spacing: 0) {
                            Text(AgentStyle.of(s.agent).name).font(.caption2.weight(.semibold)).foregroundStyle(.secondary)
                            Text(Brand.stateLabel(s.state)).font(.caption.weight(.semibold)).foregroundStyle(Brand.stateColor(s.state))
                        }
                    }
                    .padding(.leading, 4)
                }
                DynamicIslandExpandedRegion(.trailing) {
                    VStack(alignment: .trailing, spacing: 0) {
                        if s.isActive {
                            Text(timerInterval: s.startDate...Date.distantFuture, countsDown: false)
                                .font(.callout.monospacedDigit().weight(.semibold))
                                .multilineTextAlignment(.trailing)
                                .frame(maxWidth: 64)
                        }
                        if s.running > 1 {
                            Text("\(s.running) 个 Agent").font(.caption2).foregroundStyle(.secondary)
                        }
                    }
                    .padding(.trailing, 4)
                }
                DynamicIslandExpandedRegion(.center) {
                    Text(s.title).font(.subheadline.weight(.semibold)).lineLimit(1)
                }
                DynamicIslandExpandedRegion(.bottom) {
                    VStack(alignment: .leading, spacing: 6) {
                        Text(s.detail.isEmpty ? Brand.stateLabel(s.state) : s.detail)
                            .font(.caption).foregroundStyle(.secondary).lineLimit(2)
                        if let p = s.progress {
                            ProgressView(value: p) {
                                EmptyView()
                            } currentValueLabel: {
                                Text("\(s.done)/\(s.total)")
                            }
                            .tint(Brand.stateColor(s.state))
                        } else if s.isActive {
                            ProgressView(timerInterval: s.startDate...s.startDate.addingTimeInterval(600), countsDown: false) {
                                EmptyView()
                            } currentValueLabel: {
                                EmptyView()
                            }
                            .tint(Brand.stateColor(s.state))
                        }
                        if s.isWaiting {
                            Label("打开 Conduit 进行确认", systemImage: "hand.raised.fill")
                                .font(.caption2.weight(.semibold)).foregroundStyle(Brand.amber)
                        }
                    }
                    .padding(.horizontal, 4)
                }
            } compactLeading: {
                AgentGlyph(agent: s.agent, size: 16, color: Brand.stateColor(s.state))
                    .symbolEffect(.pulse, isActive: s.isActive)
            } compactTrailing: {
                CompactTrailing(state: s)
            } minimal: {
                ProgressRing(state: s, size: 18)
            }
            .keylineTint(Brand.stateColor(s.state))
            .widgetURL(URL(string: "conduit://session/\(s.sessionId)"))
        }
    }
}

private struct CompactTrailing: View {
    let state: AgentActivityAttributes.ContentState

    var body: some View {
        if state.isWaiting {
            Image(systemName: "hand.raised.fill").foregroundStyle(Brand.amber)
        } else if state.progress != nil {
            ProgressRing(state: state, size: 16)
        } else if state.isActive {
            Text(timerInterval: state.startDate...Date.distantFuture, countsDown: false)
                .monospacedDigit()
                .font(.caption2.weight(.semibold))
                .frame(maxWidth: 40)
                .foregroundStyle(Brand.clay)
        } else {
            Image(systemName: state.state == "error" ? "exclamationmark.triangle.fill" : "checkmark.circle.fill")
                .foregroundStyle(Brand.stateColor(state.state))
        }
    }
}

private struct ProgressRing: View {
    let state: AgentActivityAttributes.ContentState
    let size: CGFloat

    var body: some View {
        ZStack {
            Circle().stroke(Brand.stateColor(state.state).opacity(0.25), lineWidth: 2.5)
            if let p = state.progress {
                Circle().trim(from: 0, to: p)
                    .stroke(Brand.stateColor(state.state), style: StrokeStyle(lineWidth: 2.5, lineCap: .round))
                    .rotationEffect(.degrees(-90))
            } else {
                AgentGlyph(agent: state.agent, size: size * 0.55, color: Brand.stateColor(state.state))
            }
        }
        .frame(width: size, height: size)
    }
}

private struct LockScreenView: View {
    let state: AgentActivityAttributes.ContentState
    let hostName: String

    var body: some View {
        HStack(alignment: .top, spacing: 12) {
            ZStack {
                Circle().fill(Brand.stateColor(state.state).opacity(0.15))
                AgentGlyph(agent: state.agent, size: 22, color: Brand.stateColor(state.state))
            }
            .frame(width: 40, height: 40)

            VStack(alignment: .leading, spacing: 4) {
                HStack {
                    Text(state.title).font(.headline).lineLimit(1)
                    Spacer()
                    if state.isActive {
                        Text(timerInterval: state.startDate...Date.distantFuture, countsDown: false)
                            .font(.subheadline.monospacedDigit()).foregroundStyle(.secondary)
                            .multilineTextAlignment(.trailing).frame(maxWidth: 70)
                    }
                }
                Text(state.detail.isEmpty ? Brand.stateLabel(state.state) : state.detail)
                    .font(.subheadline).foregroundStyle(.secondary).lineLimit(2)
                if let p = state.progress {
                    ProgressView(value: p).tint(Brand.stateColor(state.state))
                }
                HStack(spacing: 6) {
                    Text(Brand.stateLabel(state.state)).foregroundStyle(Brand.stateColor(state.state))
                    Text("·")
                    Text(hostName)
                    if state.running > 1 {
                        Text("·")
                        Text("\(state.running) 个 Agent")
                    }
                }
                .font(.caption.weight(.medium)).foregroundStyle(.secondary)
            }
        }
        .padding(16)
    }
}

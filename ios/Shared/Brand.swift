import SwiftUI

/// Warm, editorial palette (ivory paper, ink, clay accent) used by the app and the widget extension.
enum Brand {
    /// Clay — the primary accent.
    static let clay = Color(red: 0.851, green: 0.467, blue: 0.341)        // #D97757
    static let clayDeep = Color(red: 0.776, green: 0.380, blue: 0.247)    // #C6613F
    static let sand = Color(red: 0.831, green: 0.635, blue: 0.498)        // #D4A27F
    static let olive = Color(red: 0.471, green: 0.549, blue: 0.365)       // #788C5D
    static let amber = Color(red: 0.902, green: 0.659, blue: 0.290)       // #E6A84A
    static let rust = Color(red: 0.749, green: 0.302, blue: 0.263)        // #BF4D43
    static let sky = Color(red: 0.416, green: 0.608, blue: 0.800)         // #6A9BCC

    static let paper = Color(light: .init(red: 0.980, green: 0.976, blue: 0.961, alpha: 1),   // #FAF9F5
                             dark: .init(red: 0.122, green: 0.118, blue: 0.114, alpha: 1))    // #1F1E1D
    static let paperDeep = Color(light: .init(red: 0.941, green: 0.933, blue: 0.902, alpha: 1), // #F0EEE6
                                 dark: .init(red: 0.149, green: 0.149, blue: 0.141, alpha: 1))  // #262624
    static let ink = Color(light: .init(red: 0.078, green: 0.078, blue: 0.075, alpha: 1),     // #141413
                           dark: .init(red: 0.961, green: 0.957, blue: 0.933, alpha: 1))      // #F5F4EE
    static let inkSoft = Color(light: .init(red: 0.369, green: 0.365, blue: 0.349, alpha: 1), // #5E5D59
                               dark: .init(red: 0.651, green: 0.639, blue: 0.604, alpha: 1))  // #A6A39A
    static let hairline = Color(light: .init(white: 0, alpha: 0.08), dark: .init(white: 1, alpha: 0.10))

    static func stateColor(_ state: String) -> Color {
        switch state {
        case "running", "starting": return clay
        case "waiting": return amber
        case "error": return rust
        case "idle": return olive
        default: return inkSoft
        }
    }

    static func stateLabel(_ state: String) -> String {
        switch state {
        case "starting": return "启动中"
        case "running": return "工作中"
        case "waiting": return "等你确认"
        case "idle": return "已完成"
        case "error": return "出错"
        default: return "已停止"
        }
    }
}

/// Visual identity for each supported agent.
struct AgentStyle {
    let name: String
    let tint: Color
    let symbol: String?
    let glyph: String?

    static func of(_ agentId: String) -> AgentStyle {
        switch agentId {
        case "claude": return AgentStyle(name: "Claude", tint: Brand.clay, symbol: nil, glyph: nil)
        case "codex": return AgentStyle(name: "Codex", tint: Color(light: .init(white: 0.15, alpha: 1), dark: .init(white: 0.92, alpha: 1)),
                                        symbol: "chevron.left.forwardslash.chevron.right", glyph: nil)
        case "pi": return AgentStyle(name: "Pi", tint: Color(red: 0.49, green: 0.42, blue: 0.86), symbol: nil, glyph: "π")
        case "opencode": return AgentStyle(name: "opencode", tint: Brand.sky, symbol: "curlybraces", glyph: nil)
        default: return AgentStyle(name: "演示", tint: Brand.olive, symbol: "sparkles", glyph: nil)
        }
    }
}

/// Agent mark: Claude gets the spark, others a symbol or glyph.
struct AgentGlyph: View {
    let agent: String
    var size: CGFloat = 14
    var color: Color?

    var body: some View {
        let style = AgentStyle.of(agent)
        Group {
            if agent == "claude" {
                SparkShape().fill(color ?? style.tint).frame(width: size, height: size)
            } else if let glyph = style.glyph {
                Text(glyph).font(.system(size: size * 1.05, weight: .semibold, design: .serif)).foregroundStyle(color ?? style.tint)
            } else {
                Image(systemName: style.symbol ?? "circle").font(.system(size: size * 0.82, weight: .semibold)).foregroundStyle(color ?? style.tint)
            }
        }
        .frame(width: size, height: size)
    }
}

/// An original radial "spark": tapered rays of varying length around a soft centre.
struct SparkShape: Shape {
    var rays = 10

    func path(in rect: CGRect) -> Path {
        let c = CGPoint(x: rect.midX, y: rect.midY)
        let r = min(rect.width, rect.height) / 2
        var p = Path()
        for i in 0..<rays {
            let angle = (Double(i) / Double(rays)) * 2 * .pi - .pi / 2
            let length = r * (i % 2 == 0 ? 1.0 : 0.78)
            let width = r * 0.16
            let tip = CGPoint(x: c.x + cos(angle) * length, y: c.y + sin(angle) * length)
            let perp = angle + .pi / 2
            let base1 = CGPoint(x: c.x + cos(perp) * width / 2, y: c.y + sin(perp) * width / 2)
            let base2 = CGPoint(x: c.x - cos(perp) * width / 2, y: c.y - sin(perp) * width / 2)
            p.move(to: base1)
            p.addQuadCurve(to: tip, control: CGPoint(x: (base1.x + tip.x) / 2 + cos(perp) * width * 0.15,
                                                     y: (base1.y + tip.y) / 2 + sin(perp) * width * 0.15))
            p.addQuadCurve(to: base2, control: CGPoint(x: (base2.x + tip.x) / 2 - cos(perp) * width * 0.15,
                                                       y: (base2.y + tip.y) / 2 - sin(perp) * width * 0.15))
            p.closeSubpath()
        }
        p.addEllipse(in: CGRect(x: c.x - r * 0.16, y: c.y - r * 0.16, width: r * 0.32, height: r * 0.32))
        return p
    }
}

/// The Conduit mark: a thick "C" (the conduit) with a four-point spark in its mouth. Matches AppIcon.icon.
struct ConduitMark: View {
    var color: Color = Brand.clay
    var sparkColor: Color?

    var body: some View {
        GeometryReader { g in
            let s = min(g.size.width, g.size.height) / 1024
            ZStack {
                ConduitArcShape().fill(color)
                FourPointSparkShape().fill(sparkColor ?? color)
            }
            .frame(width: 1024 * s, height: 1024 * s)
        }
        .aspectRatio(1, contentMode: .fit)
    }
}

struct ConduitArcShape: Shape {
    func path(in rect: CGRect) -> Path {
        let k = min(rect.width, rect.height) / 1024
        let c = CGPoint(x: rect.minX + 492 * k, y: rect.minY + 512 * k)
        var p = Path()
        p.addArc(center: c, radius: 268 * k, startAngle: .degrees(-62), endAngle: .degrees(62), clockwise: true)
        return p.strokedPath(StrokeStyle(lineWidth: 150 * k, lineCap: .round))
    }
}

struct FourPointSparkShape: Shape {
    func path(in rect: CGRect) -> Path {
        let k = min(rect.width, rect.height) / 1024
        let cx = rect.minX + 742 * k, cy = rect.minY + 512 * k, r = 132 * k
        var p = Path()
        let steps = 120
        for i in 0...steps {
            let a = Double(i) / Double(steps) * 2 * .pi
            let c = cos(a), sn = sin(a)
            let m = pow(pow(abs(c), 2.0 / 3) + pow(abs(sn), 2.0 / 3), -1.5)
            let pt = CGPoint(x: cx + r * c * m, y: cy - r * sn * m)
            if i == 0 { p.move(to: pt) } else { p.addLine(to: pt) }
        }
        p.closeSubpath()
        return p
    }
}

extension Color {
    init(light: UIColor, dark: UIColor) {
        self.init(uiColor: UIColor { $0.userInterfaceStyle == .dark ? dark : light })
    }
}

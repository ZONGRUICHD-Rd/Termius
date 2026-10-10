import SwiftUI

struct WelcomeView: View {
    @State private var showPair = false
    @State private var appear = false

    var body: some View {
        ZStack {
            PaperBackground()
            VStack(alignment: .leading, spacing: 28) {
                Spacer()
                ConduitMark()
                    .frame(width: 72, height: 72)
                    .rotationEffect(.degrees(appear ? 0 : -40))
                    .scaleEffect(appear ? 1 : 0.6)
                    .opacity(appear ? 1 : 0)

                VStack(alignment: .leading, spacing: 12) {
                    Text("你的编程 Agent，\n随时随地。")
                        .font(.serif(.largeTitle, weight: .medium))
                        .foregroundStyle(Brand.ink)
                    Text("Conduit 连接你电脑上的 Claude、Codex、Pi 和 opencode——端到端加密，支持内网直连、Cloudflare Tunnel 和 DDNS。")
                        .font(.body)
                        .foregroundStyle(Brand.inkSoft)
                        .fixedSize(horizontal: false, vertical: true)
                }
                .opacity(appear ? 1 : 0)
                .offset(y: appear ? 0 : 12)

                VStack(alignment: .leading, spacing: 14) {
                    step(1, "在 Windows 电脑上安装 Conduit Host")
                    step(2, "打开后点击“配对手机”")
                    step(3, "扫描二维码")
                }
                .opacity(appear ? 1 : 0)

                Spacer()

                Button {
                    showPair = true
                } label: {
                    Label("配对电脑", systemImage: "qrcode.viewfinder")
                        .font(.headline)
                        .frame(maxWidth: .infinity)
                        .padding(.vertical, 6)
                }
                .buttonStyle(.glassProminent)
                .controlSize(.large)
                .tint(Brand.clay)
            }
            .padding(28)
        }
        .onAppear {
            withAnimation(.smooth(duration: 0.9).delay(0.1)) { appear = true }
        }
        .sheet(isPresented: $showPair) {
            PairView()
        }
    }

    private func step(_ n: Int, _ text: String) -> some View {
        HStack(spacing: 12) {
            Text("\(n)")
                .font(.serif(.subheadline, weight: .semibold))
                .frame(width: 26, height: 26)
                .glassEffect(.regular.tint(Brand.clay.opacity(0.25)), in: .circle)
            Text(text).font(.subheadline).foregroundStyle(Brand.ink)
        }
    }
}

import SwiftUI

struct WelcomeView: View {
    @State private var showPair = false
    @State private var appear = false

    var body: some View {
        ZStack {
            PaperBackground()
            VStack(alignment: .leading, spacing: 28) {
                Spacer()
                SparkShape()
                    .fill(Brand.clay)
                    .frame(width: 64, height: 64)
                    .rotationEffect(.degrees(appear ? 0 : -40))
                    .scaleEffect(appear ? 1 : 0.6)
                    .opacity(appear ? 1 : 0)

                VStack(alignment: .leading, spacing: 12) {
                    Text("Your coding agents,\nanywhere.")
                        .font(.serif(.largeTitle, weight: .medium))
                        .foregroundStyle(Brand.ink)
                    Text("Conduit connects to Claude, Codex, Pi and opencode running on your computer — end-to-end encrypted, over your Wi‑Fi, Tailscale or a relay.")
                        .font(.body)
                        .foregroundStyle(Brand.inkSoft)
                        .fixedSize(horizontal: false, vertical: true)
                }
                .opacity(appear ? 1 : 0)
                .offset(y: appear ? 0 : 12)

                VStack(alignment: .leading, spacing: 14) {
                    step(1, "Install Conduit Host on your Windows PC")
                    step(2, "Open it and click “Pair a phone”")
                    step(3, "Scan the QR code")
                }
                .opacity(appear ? 1 : 0)

                Spacer()

                Button {
                    showPair = true
                } label: {
                    Label("Pair with a computer", systemImage: "qrcode.viewfinder")
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

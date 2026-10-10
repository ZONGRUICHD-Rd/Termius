import AVFoundation
import ConduitKit
import SwiftUI

struct PairView: View {
    @Environment(AppModel.self) private var model
    @Environment(\.dismiss) private var dismiss
    @State private var manualCode = ""
    @State private var phase: Phase = .scanning
    @State private var cameraDenied = false

    enum Phase: Equatable {
        case scanning
        case connecting(String)
        case failed(String)
        case done(String)
    }

    var body: some View {
        NavigationStack {
            ScrollView {
                VStack(spacing: 22) {
                    scanner
                    Group {
                        switch phase {
                        case .scanning:
                            Text("用相机对准电脑上 Conduit Host 显示的二维码。")
                                .font(.subheadline).foregroundStyle(.secondary).multilineTextAlignment(.center)
                        case .connecting(let name):
                            HStack(spacing: 10) {
                                ProgressView()
                                Text("正在与 \(name) 建立加密连接…").font(.subheadline)
                            }
                        case .failed(let message):
                            Label(message, systemImage: "exclamationmark.triangle.fill")
                                .font(.subheadline).foregroundStyle(Brand.rust).multilineTextAlignment(.center)
                        case .done(let name):
                            Label("已与 \(name) 配对", systemImage: "checkmark.seal.fill")
                                .font(.headline).foregroundStyle(Brand.olive)
                        }
                    }
                    .transition(.opacity.combined(with: .move(edge: .bottom)))

                    VStack(alignment: .leading, spacing: 10) {
                        EyebrowLabel(text: "或粘贴配对链接")
                        HStack(spacing: 10) {
                            TextField("conduit://pair?d=…", text: $manualCode, axis: .vertical)
                                .lineLimit(1...3)
                                .textInputAutocapitalization(.never)
                                .autocorrectionDisabled()
                                .font(.callout.monospaced())
                                .padding(.horizontal, 14).padding(.vertical, 10)
                                .glassEffect(.regular, in: .rect(cornerRadius: 18))
                            PasteButton(payloadType: String.self) { strings in
                                if let s = strings.first {
                                    manualCode = s
                                    submit(s)
                                }
                            }
                            .buttonBorderShape(.capsule)
                            .labelStyle(.iconOnly)
                        }
                        Button("连接") { submit(manualCode) }
                            .buttonStyle(.glassProminent)
                            .disabled(manualCode.isEmpty || isBusy)
                    }

                    if RuntimeEnvironment.isLiveContainer {
                        Label("正在 LiveContainer 中运行——如果相机不可用，请在电脑端复制配对链接并粘贴到这里。",
                              systemImage: "shippingbox")
                            .font(.footnote).foregroundStyle(.secondary)
                    }
                }
                .padding(20)
                .animation(.smooth, value: phase)
            }
            .paperBackground()
            .navigationTitle("配对电脑")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button(role: .cancel) { dismiss() } label: { Image(systemName: "xmark") }
                }
            }
        }
        .presentationDetents([.large])
    }

    private var isBusy: Bool {
        if case .connecting = phase { return true }
        return false
    }

    @ViewBuilder
    private var scanner: some View {
        ZStack {
            if cameraDenied {
                VStack(spacing: 10) {
                    Image(systemName: "camera.fill").font(.largeTitle).foregroundStyle(.secondary)
                    Text("相机不可用").font(.headline)
                    Text("请在系统设置中允许相机访问，或在下方粘贴链接。").font(.footnote).foregroundStyle(.secondary)
                }
            } else {
                QRScannerView { code in
                    if case .scanning = phase { submit(code) }
                    if case .failed = phase { submit(code) }
                } onUnavailable: {
                    cameraDenied = true
                }
            }
            RoundedRectangle(cornerRadius: 28)
                .strokeBorder(Brand.clay.opacity(isBusy ? 1 : 0.5), style: StrokeStyle(lineWidth: 3, dash: isBusy ? [] : [14, 10]))
                .padding(36)
                .allowsHitTesting(false)
        }
        .frame(height: 320)
        .clipShape(.rect(cornerRadius: 32))
        .background(Brand.paperDeep, in: .rect(cornerRadius: 32))
    }

    private func submit(_ text: String) {
        guard !isBusy else { return }
        do {
            let payload = try PairingPayload.parse(text)
            phase = .connecting(payload.hostName)
            Task {
                do {
                    try await model.pair(with: payload)
                    phase = .done(payload.hostName)
                    try? await Task.sleep(nanoseconds: 700_000_000)
                    model.showPairing = false
                    dismiss()
                } catch {
                    phase = .failed(error.localizedDescription)
                }
            }
        } catch {
            phase = .failed(error.localizedDescription)
        }
    }
}

/// Minimal AVFoundation QR scanner (works without VisionKit, which isn't reliable inside LiveContainer).
struct QRScannerView: UIViewControllerRepresentable {
    var onCode: (String) -> Void
    var onUnavailable: () -> Void

    func makeUIViewController(context: Context) -> ScannerController {
        let c = ScannerController()
        c.onCode = onCode
        c.onUnavailable = onUnavailable
        return c
    }

    func updateUIViewController(_ controller: ScannerController, context: Context) {
        controller.onCode = onCode
    }

    final class ScannerController: UIViewController, AVCaptureMetadataOutputObjectsDelegate {
        var onCode: ((String) -> Void)?
        var onUnavailable: (() -> Void)?
        private let session = AVCaptureSession()
        private var preview: AVCaptureVideoPreviewLayer?
        private var lastCode: String?

        override func viewDidLoad() {
            super.viewDidLoad()
            view.backgroundColor = .black
            switch AVCaptureDevice.authorizationStatus(for: .video) {
            case .authorized:
                configure()
            case .notDetermined:
                AVCaptureDevice.requestAccess(for: .video) { ok in
                    DispatchQueue.main.async { ok ? self.configure() : self.onUnavailable?() }
                }
            default:
                onUnavailable?()
            }
        }

        private func configure() {
            guard let device = AVCaptureDevice.default(for: .video), let input = try? AVCaptureDeviceInput(device: device),
                  session.canAddInput(input) else {
                onUnavailable?()
                return
            }
            session.addInput(input)
            let output = AVCaptureMetadataOutput()
            guard session.canAddOutput(output) else {
                onUnavailable?()
                return
            }
            session.addOutput(output)
            output.setMetadataObjectsDelegate(self, queue: .main)
            output.metadataObjectTypes = [.qr]
            let layer = AVCaptureVideoPreviewLayer(session: session)
            layer.videoGravity = .resizeAspectFill
            view.layer.addSublayer(layer)
            preview = layer
            DispatchQueue.global(qos: .userInitiated).async { self.session.startRunning() }
        }

        override func viewDidLayoutSubviews() {
            super.viewDidLayoutSubviews()
            preview?.frame = view.bounds
        }

        override func viewWillDisappear(_ animated: Bool) {
            super.viewWillDisappear(animated)
            if session.isRunning { DispatchQueue.global().async { self.session.stopRunning() } }
        }

        func metadataOutput(_ output: AVCaptureMetadataOutput, didOutput objects: [AVMetadataObject], from connection: AVCaptureConnection) {
            guard let code = (objects.first as? AVMetadataMachineReadableCodeObject)?.stringValue, code != lastCode else { return }
            lastCode = code
            UINotificationFeedbackGenerator().notificationOccurred(.success)
            onCode?(code)
        }
    }
}

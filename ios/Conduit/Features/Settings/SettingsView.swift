import ConduitKit
import SwiftUI

struct SettingsView: View {
    @Environment(AppModel.self) private var model
    @AppStorage(LiveActivityController.enabledKey) private var liveActivities = true
    @AppStorage("showReasoning") private var showReasoning = false
    @AppStorage("apnsEnvironment") private var apnsEnvironment = "production"
    @State private var showDemo = false

    var body: some View {
        NavigationStack {
            List {
                Section {
                    ForEach(model.hosts) { host in
                        NavigationLink {
                            HostDetailView(hostId: host.id)
                        } label: {
                            HStack(spacing: 12) {
                                Image(systemName: "desktopcomputer")
                                    .foregroundStyle(host.id == model.activeHostId ? Brand.clay : Brand.inkSoft)
                                    .frame(width: 36, height: 36)
                                    .glassEffect(host.id == model.activeHostId ? .regular.tint(Brand.clay.opacity(0.2)) : .regular, in: .circle)
                                VStack(alignment: .leading, spacing: 2) {
                                    Text(host.name).font(.body.weight(.semibold))
                                    if host.id == model.activeHostId, let client = model.client {
                                        ConnectionBadge(status: client.status, rtt: client.rttMs)
                                    } else {
                                        Text((host.preference ?? .auto).label).font(.caption).foregroundStyle(.secondary)
                                    }
                                }
                            }
                        }
                    }
                    Button {
                        model.showPairing = true
                    } label: {
                        Label("配对新电脑", systemImage: "qrcode.viewfinder")
                    }
                } header: { EyebrowLabel(text: "电脑与连接方式") }

                if model.client?.hostInfo?.agents.contains(where: { $0.id == "demo" }) == true {
                    Section {
                        Button {
                            showDemo = true
                        } label: {
                            Label("演示 Agent", systemImage: "sparkles")
                        }
                    } footer: { Text("无需安装任何 Agent 即可体验对话、审批和灵动岛。在电脑端“Agent”页开启。") }
                }

                Section {
                    Toggle(isOn: $liveActivities) {
                        Label("灵动岛与锁屏实时活动", systemImage: "platter.filled.top.iphone")
                    }
                    .disabled(!RuntimeEnvironment.liveActivitiesSupported)
                    .onChange(of: liveActivities) { _, on in
                        if !on { model.liveActivity.endNow() }
                    }
                    if !RuntimeEnvironment.liveActivitiesSupported {
                        Text(liveActivityUnavailableReason).font(.footnote).foregroundStyle(.secondary)
                    }
                    Picker("APNs 环境", selection: $apnsEnvironment) {
                        Text("正式（Production）").tag("production")
                        Text("开发（Sandbox）").tag("sandbox")
                    }
                } header: {
                    EyebrowLabel(text: "实时活动")
                } footer: {
                    Text("App 打开时由手机直接更新灵动岛。要在后台也持续更新，请在电脑端“设置”中填写 APNs 密钥。")
                }

                Section {
                    Toggle(isOn: $showReasoning) { Label("显示 Agent 的思考过程", systemImage: "brain") }
                } header: { EyebrowLabel(text: "对话") }

                Section {
                    LabeledContent("版本", value: RuntimeEnvironment.appVersion)
                    LabeledContent("运行环境", value: RuntimeEnvironment.isLiveContainer ? "LiveContainer" : "原生")
                    LabeledContent("设备密钥") {
                        Text(model.identity.deviceId).font(.caption.monospaced()).foregroundStyle(.secondary)
                    }
                    LabeledContent("加密", value: "P-256 ECDH · AES-256-GCM")
                } header: { EyebrowLabel(text: "关于") } footer: {
                    Text("手机与电脑之间的所有通信都是端到端加密的，Cloudflare 和中继只能看到密文。")
                }
            }
            .scrollContentBackground(.hidden)
            .paperBackground()
            .navigationTitle("设置")
            .sheet(isPresented: $showDemo) {
                AgentHomeView(agent: "demo").presentationDetents([.large])
            }
        }
    }

    private var liveActivityUnavailableReason: String {
        if RuntimeEnvironment.isLiveContainer {
            return "LiveContainer 无法运行 App 扩展，因此改用 App 内的“岛”显示进度。"
        }
        if !RuntimeEnvironment.hasWidgetExtension {
            return "此安装包不含小组件扩展，改用 App 内的“岛”显示进度。"
        }
        return "请在 iOS 设置中为 Conduit 开启“实时活动”。"
    }
}

/// One paired computer: how this phone connects to it, and the computer's own connection settings.
struct HostDetailView: View {
    let hostId: String

    @Environment(AppModel.self) private var model
    @Environment(\.dismiss) private var dismiss
    @State private var manual = ""
    @State private var probes: [ConduitClient.RouteProbe] = []
    @State private var probing = false
    @State private var confirmRemove = false

    // Computer-side settings (editable when connected).
    @State private var lan = true
    @State private var cfEnabled = false
    @State private var cfMode = "quick"
    @State private var cfToken = ""
    @State private var cfHostname = ""
    @State private var ddnsEnabled = false
    @State private var ddnsDomain = ""
    @State private var ddnsPort = "47821"
    @State private var ddnsTLS = false
    @State private var saving = false
    @State private var saveMessage: String?

    private var host: PairedHost? { model.hosts.first { $0.id == hostId } }
    private var isActive: Bool { model.activeHostId == hostId }
    private var info: HostInfo? { isActive ? model.client?.hostInfo : nil }

    var body: some View {
        List {
            if let host {
                Section {
                    Picker("使用的连接方式", selection: preferenceBinding(host)) {
                        ForEach(RoutePreference.allCases) { p in Text(p.label).tag(p) }
                    }
                    ForEach(Endpoint.candidates(for: host), id: \.self) { e in
                        HStack {
                            Label(e.kind.label, systemImage: e.kind.symbol).font(.subheadline)
                            Spacer()
                            if let probe = probes.first(where: { $0.endpoint == e }) {
                                if let ms = probe.milliseconds {
                                    Text("\(ms) ms").font(.caption.monospacedDigit()).foregroundStyle(Brand.olive)
                                } else {
                                    Text("不可用").font(.caption).foregroundStyle(Brand.rust)
                                }
                            } else {
                                Text(e.url.host ?? "").font(.caption.monospaced()).foregroundStyle(.secondary).lineLimit(1)
                            }
                        }
                    }
                    if isActive {
                        Button {
                            probe()
                        } label: {
                            if probing { ProgressView() } else { Label("测试每条线路", systemImage: "speedometer") }
                        }
                    } else {
                        Button("切换到这台电脑") { model.connect(to: hostId) }
                    }
                } header: {
                    EyebrowLabel(text: "这台手机")
                } footer: {
                    Text("“自动”会同时尝试所有线路并使用最快的一条；在外网时会在内网可用后自动切回直连。")
                }

                Section {
                    ForEach(host.manualEndpoints ?? [], id: \.self) { e in
                        Text(e).font(.callout.monospaced())
                    }
                    .onDelete { idx in
                        var h = host
                        h.manualEndpoints?.remove(atOffsets: idx)
                        save(h)
                    }
                    HStack {
                        TextField("home.example.com:47821 或 wss://…", text: $manual)
                            .textInputAutocapitalization(.never).autocorrectionDisabled().keyboardType(.URL)
                        Button("添加") {
                            var h = host
                            h.manualEndpoints = (h.manualEndpoints ?? []) + [manual.trimmingCharacters(in: .whitespaces)]
                            manual = ""
                            save(h)
                        }
                        .disabled(manual.trimmingCharacters(in: .whitespaces).isEmpty)
                    }
                } header: {
                    EyebrowLabel(text: "手动添加地址（DDNS / Cloudflare 域名）")
                }

                if isActive {
                    if let info {
                        computerSettings(info)
                    } else {
                        Section { Text("连接上电脑后可以在这里修改电脑端的连接设置。").foregroundStyle(.secondary) }
                    }
                }

                Section {
                    Button("忘记这台电脑", role: .destructive) { confirmRemove = true }
                }
            }
        }
        .scrollContentBackground(.hidden)
        .paperBackground()
        .navigationTitle(host?.name ?? "电脑")
        .onAppear(perform: loadFromInfo)
        .onChange(of: info?.routes) { _, _ in loadFromInfo() }
        .confirmationDialog("忘记 \(host?.name ?? "")？", isPresented: $confirmRemove, titleVisibility: .visible) {
            Button("忘记", role: .destructive) {
                model.removeHost(hostId)
                dismiss()
            }
        } message: {
            Text("之后需要重新扫码配对。也建议在电脑端移除这台手机，以撤销它的密钥。")
        }
    }

    @ViewBuilder
    private func computerSettings(_ info: HostInfo) -> some View {
        Section {
            Toggle("内网直连 / Tailscale", isOn: $lan)
        } header: {
            EyebrowLabel(text: "电脑端设置 · \(info.hostName)")
        }
        Section {
            Toggle("Cloudflare Tunnel", isOn: $cfEnabled)
            if cfEnabled {
                Picker("类型", selection: $cfMode) {
                    Text("快速隧道（免费，地址会变）").tag("quick")
                    Text("自有域名（令牌，地址固定）").tag("token")
                }
                if cfMode == "token" {
                    SecureField("隧道令牌（Tunnel Token）", text: $cfToken)
                    TextField("公共主机名，如 conduit.example.com", text: $cfHostname)
                        .textInputAutocapitalization(.never).autocorrectionDisabled().keyboardType(.URL)
                }
                HStack {
                    Text("状态")
                    Spacer()
                    Text(tunnelStatus(info.cloudflareStatus)).foregroundStyle(info.cloudflareStatus == "online" ? Brand.olive : .secondary)
                }
                if let url = info.cloudflareURL {
                    Text(url).font(.caption.monospaced()).textSelection(.enabled)
                }
            }
        } footer: {
            Text(cfMode == "token"
                 ? "在 Cloudflare Zero Trust 中创建隧道，并添加公共主机名，服务填写 http://localhost:47821。"
                 : "无需账号，电脑会自动下载 cloudflared。每次电脑端重启地址会变化，变化后需在内网连一次以更新。")
        }
        Section {
            Toggle("DDNS / 端口映射", isOn: $ddnsEnabled)
            if ddnsEnabled {
                TextField("域名，如 home.example.com", text: $ddnsDomain)
                    .textInputAutocapitalization(.never).autocorrectionDisabled().keyboardType(.URL)
                TextField("外网端口", text: $ddnsPort).keyboardType(.numberPad)
                Toggle("使用 TLS（wss://）", isOn: $ddnsTLS)
            }
        } footer: {
            Text("需要在路由器上把外网端口转发到电脑的 47821 端口。")
        }
        Section {
            Button {
                saveComputer()
            } label: {
                if saving { ProgressView() } else { Text("保存到电脑").bold() }
            }
            .disabled(saving)
            if let saveMessage {
                Text(saveMessage).font(.footnote).foregroundStyle(.secondary)
            }
        }
    }

    private func tunnelStatus(_ s: String) -> String {
        switch s {
        case "online": return "已连接"
        case "starting": return "启动中"
        case "error": return "出错"
        default: return "未开启"
        }
    }

    private func loadFromInfo() {
        guard let info else { return }
        lan = info.lanEnabled
        cfEnabled = info.cloudflareEnabled
        cfMode = info.cloudflareMode
        ddnsEnabled = info.ddnsEnabled
        ddnsDomain = info.ddnsDomain
        ddnsPort = String(info.ddnsPort)
        ddnsTLS = info.ddnsTLS
    }

    private func preferenceBinding(_ host: PairedHost) -> Binding<RoutePreference> {
        Binding(
            get: { host.preference ?? .auto },
            set: { p in
                var h = host
                h.preference = p == .auto ? nil : p
                save(h)
                if isActive { model.client?.reconnect() }
            })
    }

    private func save(_ h: PairedHost) {
        model.saveHost(h)
    }

    private func probe() {
        guard let client = model.client else { return }
        probing = true
        Task {
            probes = await client.probeRoutes()
            probing = false
        }
    }

    private func saveComputer() {
        guard let store = model.store else { return }
        saving = true
        saveMessage = nil
        var cf: [String: JSONValue] = ["enabled": .bool(cfEnabled), "mode": .string(cfMode)]
        if cfMode == "token" {
            if !cfToken.isEmpty { cf["token"] = .string(cfToken) }
            cf["hostname"] = .string(cfHostname)
        }
        let params: [String: JSONValue] = [
            "lan": .bool(lan),
            "cloudflare": .object(cf),
            "ddns": ["enabled": .bool(ddnsEnabled), "domain": .string(ddnsDomain), "port": .int(Int64(Int(ddnsPort) ?? 47821)), "tls": .bool(ddnsTLS)],
        ]
        Task {
            do {
                try await store.setConnection(params)
                saveMessage = "已保存。新的地址已同步到这台手机。"
            } catch {
                saveMessage = error.localizedDescription
            }
            saving = false
        }
    }
}

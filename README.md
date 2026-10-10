<p align="center"><img src="tools/icon-preview.png" width="260" alt="Conduit"></p>

<h1 align="center">Conduit</h1>

<p align="center">用 iPhone 随时随地使用电脑上的编程 Agent —— Claude · Codex · Pi · opencode</p>

---

Conduit 由三部分组成：

| 组件 | 技术 | 作用 |
|---|---|---|
| **Conduit (iOS)** | SwiftUI · Liquid Glass · ActivityKit · Network.framework | 手机端：会话列表、流式对话、审批、Dynamic Island 实时进度 |
| **Conduit Host (Windows)** | WinUI 3 · .NET 8 · Kestrel | 电脑端：发现并驱动本机 Agent，端到端加密地把它们暴露给手机 |
| **Conduit Relay（可选）** | ASP.NET Core · Docker | 无公网 IP / 不想装 Tailscale 时的中继，只转发密文 |

## 功能

**像 Claude Code / Codex 手机版一样用**
- 每个 Agent 一个独立栏目（Claude / Codex / Pi / opencode），打开就是对话：在底部选好项目、直接描述任务即可开始
- 流式输出、工具调用卡片、计划进度、权限审批（允许 / 始终允许 / 拒绝）、中断，随时补充指示
- 切换模型和权限模式（Claude 计划模式 / 自动接受编辑 / 完全自主；Codex 只读 / 自动 / 完全访问；opencode 构建 / 计划），可以发送截图
- **代码变更**：文件列表和 +/− 行数、逐个文件看 diff、提交、推送、新建分支、创建 Pull Request
- **继续电脑上的对话**：列出在电脑上用 Claude Code / Codex 进行过的对话，从手机接着继续
- 灵动岛 / 锁屏实时活动：显示正在运行的 Agent、当前步骤、进度、计时和待确认事项；在 LiveContainer 中改用 App 内的液态玻璃“岛”

**四个 Agent，各用各自官方的程序接口**
- **Claude**（Claude Desktop / Claude Code）：stream-json + stdio 权限确认。Claude Desktop 的聊天窗口没有远程控制接口，所以 Conduit 驱动的是它内置的 Claude Code 引擎，账号和配置都相同
- **Codex**（Codex Desktop / CLI）：`codex app-server`，与 Codex 桌面版和 IDE 插件用的是同一套 JSON-RPC 接口
- **Pi**：`pi --mode rpc`
- **opencode**：`opencode serve`（只监听本机，并用随机密码保护）

**连接方式（电脑端和手机端都可以设置）**
- **内网直连 / Tailscale**：速度最快，会自动识别
- **Cloudflare Tunnel**：快速隧道免费、不用账号；也可以用自有域名的隧道令牌，地址固定。cloudflared 会自动下载
- **DDNS / 端口映射**：填写域名和端口即可
- **中继**（可选）：自己部署，只转发密文
- 手机会同时尝试所有允许的线路，用最快的一条。走外网线路时，一旦内网可用，会自动无感地切回直连。也可以在手机上指定只用某一种方式，并测试每条线路的延迟
- 全程端到端加密：P-256 ECDH（临时密钥 + 静态密钥）、HKDF-SHA256、AES-256-GCM。Cloudflare 和中继只能看到密文

**体验**
- 遵循 Apple 的液态玻璃设计规范：玻璃材质只用在悬浮于内容之上的导航和控件层，内容层保持纸张质感。`GlassEffectContainer` 用于组合相邻的玻璃元素，`glassEffectID` 用于形变动画，主要操作用 `.glassProminent`
- 新图标是 Icon Composer 格式的分层图标，系统会自动渲染液态玻璃效果，并支持浅色、深色和着色模式
- 默认中文

## 快速开始

1. **电脑端**：从 [Releases](../../releases/latest) 下载 `ConduitHost-win-x64.zip`（ARM 电脑选 arm64），**完整解压到一个新文件夹**后运行 `ConduitHost.exe`。
   - 首次运行时允许 Windows 防火墙（或在“设置 → 允许通过 Windows 防火墙”中添加）。
   - 在“Agent”页确认已检测到 Claude / Codex / Pi / opencode；没有安装的话，页面上有安装命令。想先体验，可以打开“演示 Agent”。
2. **手机端**：从 Releases 下载 IPA：
   - `Conduit.ipa`：完整版（含灵动岛扩展），用 AltStore / SideStore / Sideloadly / 自己的证书签名安装
   - `Conduit-LiveContainer.ipa`：LiveContainer 专用版
3. 在电脑端打开“配对手机”，用 App 扫码（或复制链接后粘贴到 App）即可。

### 在外网使用

在电脑端“连接方式”页（或手机“设置 → 电脑”中）任选一种或多种：

- **Cloudflare Tunnel**：打开开关即可。快速隧道免费、不用账号，但每次电脑端重启后地址会变；有自己的域名时，建议在 Cloudflare Zero Trust 中创建隧道，填入令牌和公共主机名（服务填 `http://localhost:47821`），这样地址固定。
- **DDNS / 端口映射**：在路由器上把外网端口转发到电脑的 47821 端口，然后填写域名和外网端口。
- **Tailscale**：电脑和手机都安装并登录同一个账号，Conduit 会自动识别，走点对点直连。
- **自建中继**：`cd relay && RELAY_DOMAIN=relay.example.com docker compose up -d`，然后在“连接方式”中填写 `wss://relay.example.com`。详见 [relay/README.md](relay/README.md)。

出现问题时，日志位于 `%LOCALAPPDATA%\Conduit\host.log`，启动错误记录在 `crash.log`。

## LiveContainer

Conduit 刻意避开了 LiveContainer 无法提供的能力，因此可以作为 guest App 正常运行：

| LiveContainer 限制 | Conduit 的做法 |
|---|---|
| 不能运行 App Extension（没有 Live Activity UI） | 自动检测并改用 **App 内 Island**：顶部液态玻璃胶囊，点按形变展开，可直接审批；底部 Tab Accessory 显示运行中的 Agent |
| guest 的 Info.plist ATS 设置不生效 | 使用 Network.framework 的 WebSocket，不受 ATS 限制，`ws://` 直连局域网 / Tailscale 正常 |
| 没有 App Group / 推送权限 | 不依赖 App Group；APNs 仅作为可选增强 |
| Keychain 访问组可能不可用 | Keychain 失败时自动回退到受数据保护的文件 |
| 相机可能不可用 | 支持粘贴配对链接（主机端一键复制） |
| VisionKit 扫码不可靠 | 使用 AVFoundation 扫码 |

## 构建

所有产物都由 GitHub Actions 构建：

| Workflow | 运行环境 | 产物 |
|---|---|---|
| `ios.yml` | macOS（自动选最新 Xcode，有 iOS 27 SDK 则用之） | ConduitKit 单元测试；`Conduit.ipa`、`Conduit-LiveContainer.ipa`（未签名） |
| `windows.yml` | Windows | 主机核心测试（协议、加密向量、四个适配器、端到端）；WinUI 3 x64 / arm64 自包含 zip（含 CLI 版） |
| `relay.yml` | Ubuntu | 经真实中继的端到端测试；Docker 镜像（打 tag 时推送到 GHCR） |

推送 `v*` tag 会自动创建 Release。

本地开发：

```bash
# iOS（macOS + Xcode 26/27）
brew install xcodegen && cd ios && xcodegen generate && open Conduit.xcodeproj

# Windows 主机（Visual Studio 2022 + Windows App SDK）或命令行
dotnet test windows/tests/Conduit.Host.Core.Tests
dotnet run --project windows/src/Conduit.Host.Cli -- --demo     # 无界面主机，终端里打印配对二维码
```

## 目录

```
ios/
  ConduitKit/          Swift 包：协议、加密、Network.framework 传输、路由竞速客户端（swift test）
  Conduit/             SwiftUI App（Liquid Glass UI、会话、聊天、配对、App 内 Island）
  ConduitWidgets/      Live Activity（Dynamic Island + 锁屏）
  Shared/              App 与扩展共享：ActivityAttributes、品牌色、Agent 图标
  project.yml          XcodeGen 工程定义
windows/
  src/Conduit.Host.Core/   主机引擎：会话、Agent 适配器、加密连接、Kestrel、中继客户端、APNs
  src/Conduit.Host/        WinUI 3 桌面应用
  src/Conduit.Host.Cli/    无界面主机
  tests/                   xUnit 测试
relay/                 中继服务器 + Dockerfile + docker-compose（Caddy 自动 HTTPS）
docs/PROTOCOL.md       线协议规范
```

## 设计说明

- **iOS 27 / iPhone 18 Pro**：部署目标 iOS 26（Liquid Glass API 起始版本），在 iOS 27 上运行；CI 自动使用 runner 上最新的 Xcode / SDK。界面针对 Dynamic Island 机型设计，App 内 Island 贴合灵动岛下沿。
- 图标与 App 内标志由 `tools/make_icon.py` 生成（原创图形，不使用任何第三方商标）。
- Claude 风格的配色与排版是“致敬”而非复制：火花图标为原创几何图形，不使用 Anthropic 的商标或 Logo。

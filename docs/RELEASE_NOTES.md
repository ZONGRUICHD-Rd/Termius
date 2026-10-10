用 iPhone 在任何地方驾驭电脑上的编程 Agent —— Claude · Codex · Pi · opencode。

**1.0.1**：修复 Windows 主机打开后无窗口、直接退出的问题（缺少编译后的 XAML 资源 `ConduitHost.pri`）。启动失败时会弹出错误提示，并写入 `%LOCALAPPDATA%\Conduit\crash.log`。CI 现在会实际启动 exe，确认窗口和端口正常后才发布。

## 下载

| 文件 | 用途 |
|---|---|
| `Conduit.ipa` | iPhone App 完整版（含 Dynamic Island 实时活动扩展）。未签名，用 AltStore / SideStore / Sideloadly 或自己的证书签名安装 |
| `Conduit-LiveContainer.ipa` | LiveContainer 专用版（去掉了 LiveContainer 无法运行的扩展，改用 App 内 Island） |
| `ConduitHost-win-x64.zip` / `ConduitHost-win-arm64.zip` | Windows 主机（WinUI 3，免安装，解压运行 `ConduitHost.exe`；`cli/` 内为无界面版） |
| `conduit-relay-*.zip` | 可选中继服务器单文件版（Linux x64 / arm64、Windows x64） |
| `conduit-relay-docker.zip` | 中继的 Dockerfile + docker-compose（Caddy 自动 HTTPS） |

中继 Docker 镜像：`ghcr.io/zongruichd-rd/conduit-relay`

## 开始使用

1. 在 Windows 上运行 `ConduitHost.exe`，允许防火墙；在 *Agents* 页确认已检测到你的 Agent（也可打开 Demo agent 先试用）。
2. iPhone 安装 IPA，打开后扫描主机 *Pair a phone* 页的二维码（或粘贴配对链接）。
3. 在外网使用：电脑和手机都装 Tailscale（自动直连），或部署中继并在主机设置里填写 `wss://` 地址。

## 说明

- Claude Desktop 没有对外的远程控制接口，Conduit 驱动的是它自带的 Claude Code 引擎（同账号、同配置）。
- 后台持续更新灵动岛需要在主机设置里配置 APNs 密钥并使用自己的开发者证书签名；否则 App 在前台时实时更新，后台时计时继续走。
- 本版本已通过自动化测试与 CI 构建，尚未在真机和真实 Agent 上做完整联调，欢迎反馈问题。

完整文档见仓库 README 与 `docs/PROTOCOL.md`。

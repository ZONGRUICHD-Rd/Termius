用 iPhone 随时随地使用电脑上的编程 Agent —— Claude · Codex · Pi · opencode。

## 1.1.0 更新

**Windows 电脑端**
- 修复打不开的问题：启动前会检查安装文件是否完整（缺少 `resources.pri` 等文件时会提示重新完整解压）；再次双击会把已运行的窗口调到前面；如果旧进程卡住没有窗口，会询问是否结束它并重新启动；任何启动失败都会弹出提示，并写入 `%LOCALAPPDATA%\Conduit\crash.log`，运行日志在 `%LOCALAPPDATA%\Conduit\host.log`
- 发布前会在 x64 和 ARM64 的 Windows 上解压最终的 ZIP 并实际启动，检查窗口、每个页面以及端口
- 界面改为中文；新增“连接方式”页：内网直连、Cloudflare Tunnel（快速隧道或自有域名令牌，自动下载 cloudflared）、DDNS（域名 + 端口），以及中继

**iPhone**
- 每个 Agent 一个独立栏目：Claude / Codex / Pi / opencode / 设置
- 打开就是对话界面：在底部输入框选好项目，直接描述任务就能开始，和 Claude Code 手机版的用法一致
- 对话中可以切换模型和权限模式（Claude：默认 / 自动接受编辑 / 计划模式 / 完全自主；Codex：只读 / 自动 / 完全访问；opencode：构建 / 计划），也可以发送截图
- 代码变更：查看改动了哪些文件和 +/− 行数，逐个文件看 diff，提交、推送、新建分支、创建 Pull Request（和 Codex 手机版一致）
- 继续电脑上的对话：可以列出在电脑上用 Claude Code / Codex 进行过的对话，从手机接着继续
- 连接方式可以在手机上选择（自动 / 仅内网 / 仅 Cloudflare / 仅 DDNS / 中继），能手动添加域名地址，能测试每条线路的延迟，也能直接修改电脑端的连接设置
- 默认中文；新的分层 Liquid Glass 图标，支持浅色、深色和着色模式

## 下载

| 文件 | 用途 |
|---|---|
| `Conduit.ipa` | iPhone App 完整版（含灵动岛实时活动扩展）。未签名，用 AltStore / SideStore / Sideloadly 或自己的证书签名安装 |
| `Conduit-LiveContainer.ipa` | LiveContainer 专用版（去掉了 LiveContainer 无法运行的扩展，改用 App 内的“岛”显示进度） |
| `ConduitHost-win-x64.zip` / `ConduitHost-win-arm64.zip` | Windows 电脑端（免安装）。**请把整个 ZIP 完整解压到一个新文件夹**，再运行 `ConduitHost.exe` |
| `conduit-relay-*.zip` / `conduit-relay-docker.zip` | 可选的中继服务器 |

## 开始使用

1. 在 Windows 上运行 `ConduitHost.exe`，允许 Windows 防火墙。在“Agent”页确认已检测到要用的 Agent。
2. 在 iPhone 上安装 IPA，打开后扫描电脑端“配对手机”页的二维码（也可以粘贴配对链接）。
3. 要在外网使用：在电脑端“连接方式”中开启 Cloudflare Tunnel 或 DDNS，或在电脑和手机上都安装 Tailscale。

## 说明

- Claude Desktop 没有对外开放的远程控制接口。Conduit 驱动的是它内置的 Claude Code 引擎（同一个账号、同一套配置）。
- 让灵动岛在后台也持续更新，需要在电脑端填写 APNs 密钥，并用你自己的开发者证书签名 App。
- 这个版本已经通过自动化测试和 CI 的启动检查，但还没有在真机上，以及在真实的 Agent、Cloudflare 和 DDNS 环境里完整测试过。遇到问题时请附上 `host.log`。

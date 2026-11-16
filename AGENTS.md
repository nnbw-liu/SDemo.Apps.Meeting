# AGENTS · sdemo.meeting 子仓

> 路由文件。跨产品文档在 `../../docs/`（相对本仓根）。

## 职责

录音、本地转写、议题/决议/候选、分析履历；会议横切多个项目，不隶属任何项目。

## 现状（2026-11-16）

✅ **阶段 A 已落地**：8 个工程 + `SDemo.Meeting.slnf`，`import → transcribe → list/show` 端到端跑通。
✅ **阶段 B 全部完成（B1–B7）**：VAD 分段（含窗口打包）、增量落库/取消/续转、模型库（打包 + 导入 + 校验 + 清理）、低配档自动回落、hotwords、语言 hint + 质检告警、转写结果页（WPF + BlazorWebView）。
A5 双路采集代码完成，但本机被 Windows 麦克风隐私拒绝（0x80070005），需在有权限的机器上复验。
✍️ **MCP 写面已开**：6 个写工具走 `MeetingDomainService`，成功与失败都落 `mtg_audit`（参数只存 300 字摘要）；`meeting audit` 可查。
🏷️ **领域模型已建**：`mtg_topic` / `mtg_decision` / `mtg_action`（schema v3，老库就地迁移）；候选转 `intaken` 必须带 `plannerRef`。
📚 **P3 写入链路完成**：会议 → KB 的 `ingest` + `promote`（归档 = 同一 doc 升 durable）、内容哈希幂等键、KB 不在时只记失败并在启动补投。**KB 本机未安装**，真实 ingest 未联调。
🗂️ **P2 主体完成**：三栏 + 右栏检视（议题增删、确认决议、候选拒绝、最近改动），段落可「标为决议/标为候选」并带时间码 anchor；UI 写操作也落审计（actor=User）。
🎙️ **P1 主体完成**：录音卡（开始/计时/停止并转写/取消/进度）+ 双轨开关；状态机在 `Core.RecordingCoordinator`，采集与引擎注入，无麦克风也能测。本机仍被 Windows 麦克风权限拒绝（0x80070005），真机录音待放开。
🖥️ **主程序 P0 完成**：`SDemo.Meeting.exe` = WPF 外壳 + BlazorWebView + 进程内 MCP（不再有独立 Viewer）；关窗即停服务、runtime 文件转 `stopped`；日志在 `<数据根>\meeting.log`。
🔌 **MCP 服务已接**：`meeting serve` 起回环 HTTP（`/mcp` + Bearer token、`/v1/health` 免鉴权、`/v1/shutdown` 优雅退出），启动写 `%LOCALAPPDATA%\SDemo\Hub\apps\sdemo.meeting.json`、退出改 `state=stopped`。

| 层 | 状态 |
| --- | --- |
| `Contracts` | 枚举 / DTO / `IAsrProvider` 能力契约 / 错误码 / UUIDv7 / WAV 头解析 |
| `Storage` | schema v4：`mtg_meeting` / `mtg_audio_asset` / `mtg_transcript_run` / `mtg_segment` / `mtg_meta` + `mtg_topic` / `mtg_decision` / `mtg_action` + `mtg_audit` + `mtg_projection` |
| `Core` | `MeetingService`、`TranscriptionRunner`、`TranscriptQuality`、`MeetingDomainService`、`MeetingAuditLog`、`MeetingQuery`、`TranscriptEditor`、`RecordingCoordinator`、`KbProjectionService`（KB 投影）、`KbConnection`/`HttpKbTransport` |
| `Asr` | `WhisperCppProvider`（Whisper.net 1.8.1）、`ChatAudioProvider`（回环/云端 chat）、`DeepSeekToolsProvider`（`/v1/tools/asr`）、模型目录 |
| `Audio` | `DualTrackRecorder`（WASAPI 麦克风 + 环回，带启动超时看门狗）、`WavDownmixer` |
| `Mcp` | `MeetingMcpServer`（JSON-RPC：initialize / ping / tools/list / tools/call）+ 7 个只读 + 6 个写工具（add_topic / update_topic / add_decision / confirm_decision / add_action / mark_action），写工具 risk=medium、approval=once、每次调用落 `mtg_audit` |
| `Web` | `MainShell.razor`（错误边界）、`MeetingResult.razor`（三栏）、`DomainPanel.razor`（议题/决议/候选/审计）、`RecordPanel.razor`（录音卡） |
| `Host` | **主程序**：WPF 外壳 + BlazorWebView + 进程内 MCP（`SDemo.Meeting.exe`）；带参时是 CLI：`record` / `import` / `transcribe` / `resume` / `list` / `show` / `models` / `audit` / `doctor` / `tools` / `serve` |

## 开工先做三件事

1. **阶段 A/B、领域模型、MCP 读写面、P0–P3 都完成了**。下一步按 [docs/app-plan.md](docs/app-plan.md)：**P4 分析链路**（Agent 侧 map-reduce 抽议题/决议/候选）、P1-4 录音小组件、P3 剩余（列表徽章 / `/v1/refs` 登记 / 真实 KB 联调）。
2. 主程序已经是单进程形态（`SDemo.Meeting.exe`），**别再拆出第二个 exe**——界面和 MCP 必须同一个库、同一个生命周期。
3. `workspaces/meeting.json` 已是 `active`。

模型侧：默认档由安装包自带（`tools/pack-models.ps1`），用户在 `meeting models` 里能看到状态、导入自己的模型、校验与清理。

## 必读

| 改什么 | 读 |
| --- | --- |
| 需求与验收 | [docs/requirements.md](docs/requirements.md) |
| 会议领域模型 | [docs/meeting-design.md](docs/meeting-design.md) |
| **ASR 运行时与开发计划（v0.4，含实测性能表与阶段进度）** | [docs/asr-runtime.md](docs/asr-runtime.md) |
| **主程序实施计划（P0–P4）** | [docs/app-plan.md](docs/app-plan.md) |
| 跨应用联动（候选转待办） | [../../docs/integration.md](../../docs/integration.md) §3 |
| 组件与宿主 | [../../docs/contracts/](../../docs/contracts/) |

## 命令

```powershell
dotnet build SDemo.Meeting.slnf -c Release
dotnet test  tests\SDemo.Meeting.Tests -c Release

# CLI（模型放 <数据根>\models\ggml-large-v3-turbo-q8_0.bin）
$env:SDEMO_MEETING_MODEL_DIR = "D:\models"
src\SDemo.App.Meeting.Host\bin\Release\net10.0-windows10.0.19041.0\SDemo.Meeting.exe doctor
src\SDemo.App.Meeting.Host\bin\Release\net10.0-windows10.0.19041.0\SDemo.Meeting.exe import --audio 会议.wav --transcribe

# 真实模型集成测试（默认跳过）
$env:SDEMO_WHISPER_MODEL = "D:\models\ggml-large-v3-turbo-q8_0.bin"
$env:SDEMO_WHISPER_SAMPLE = "D:\samples\zh-60s.wav"
dotnet test tests\SDemo.Meeting.Tests -c Release

# 主程序（双击同效；带参数才走 CLI）
src\SDemo.App.Meeting.Host\bin\Release\net10.0-windows10.0.19041.0\SDemo.Meeting.exe --gui --instance dev --data-dir <数据根>

# MCP 服务（给 DesktopAgent 用）
src\SDemo.App.Meeting.Host\bin\Release\net10.0-windows10.0.19041.0\SDemo.Meeting.exe serve --port 18080 --show-token
```

## 铁律

1. **零模型凭证**：不引入 `IChatClient` / `ApiKey`；分析由 DesktopAgent 调用。
2. **音频不出本机**：默认本地 whisper；云端 ASR 只能是用户显式开启的例外，并标红提示。
3. **候选不直接建待办**：`mtg_action` 只有 `candidate / intaken / rejected` 三态，转待办由 Agent 做。
4. **议题可挂任意项目**：`mtg_topic.refs` 只存 URI 字符串，不校验对方内部结构。
5. **软删 + 保留策略**：录音默认 90 天，可改永久；删除进回收站，清理前提示。

## 常见坑

- 1 小时会议本地转写要 12–60 分钟，必须后台可取消，不能阻塞主窗口。
- 双路采集（`room` / `online`）要分别转写后按时间合并，不能先混音再转写。
- 说话人 v1 手动标注；`IDiarizationProvider` 先留空实现，别提前接模型。
- 模型文件（数百 MB）**不许进 git**：放 `.gitignore`，首次运行按需下载并校验 sha256。
- **权重两条来源**：安装包用 `tools/pack-models.ps1` 打（校验 sha256 后写 `models.json`）；用户侧走 `meeting models --import <文件> [--id <packId>]`。新增模型只改 `Contracts/ModelPacks.cs` + `pack-models.ps1` 两张表，两处必须一致。
- **别自己手写 whisper 的 P/Invoke 结构体**：`whisper_full_default_params` 按值返回 struct，用 `IntPtr` 接会直接 `0xC0000005`。用 Whisper.net（同一份 ggml-org 原生库）。
- **WASAPI 采集会挂死**：无麦克风权限 / 虚拟声卡会返回 `0x80070005`，老的 `waveInOpen` 甚至直接阻塞。`DualTrackRecorder` 用看门狗（启动 10 s、首包 5 s）保证快速失败，别退回同步调用。
- **云端 ASR 的口径要以实测为准**：DeepSeek 的 `/v1/chat/completions` **不收音频**（`deepseek-flash` 的 `input_modalities` 只有 text/image，`input_audio` 会 422）；能用的是独立端点 `POST /v1/tools/asr`（multipart，≤8 MB / ≤120 秒，无时间戳）。
- **Key 只走环境变量或命令行参数**（`SDEMO_ASR_API_KEY`），不落库、不进日志、不进仓；云端调用必须 `--allow-remote` 且每次打印"音频将上传"。产品化路径是 Agent 中继，不是 Meeting 直连。
- **VAD 必须打包窗口**：能量 VAD 会在每个自然停顿处断开，81 秒音频切出 27 段 = 27 次模型调用。`VadSegmenter` 的 `MaxPackGapMs`（默认 4 秒）负责把短停顿并回一个窗口，别删。
- **分段引擎按 `NeedsVad` 分流**：whisper 自带长音频能力（整段一次），chat-audio / deepseek-tools 必须切段。判断依据是能力声明，不是引擎名。
- **`HttpListener` 对空 POST 回 411**：`POST /v1/shutdown` 这类无 body 的请求必须带 `Content-Length: 0`（curl 用 `-d ''`），否则在进到处理函数之前就被拒。
- **写工具必须落审计**：`MeetingMcpServer` 只在 `MeetingToolCatalog.WriteToolNames` 命中时审计；新增写工具记得把 `ReadOnly: false`（名单自动派生，别手写第二份）。
- **审计只存 300 字摘要**：正文不进 `mtg_audit`，否则审计表会变成第二份业务数据。
- **KB 投影默认只写结论**：议题/决议/候选；转写只在用户点「归档到知识库」时写（KB 契约默认不进转写，产品决定要证据链）。
- **候选转 `intaken` 必须带 `plannerRef`**（`sdemo.planner/...`）：没有引用说明"安排"没发生，`MeetingDomainService` 会直接拒绝。
- **别在 `OnParametersSet` 里回调父组件**：参数设置期间重入渲染会让 WebView 渲染器静默卡死（窗口空白、日志无异常）。父组件回调只在用户点击时发。
- **窗口空白先看 `meeting.log` 的「页面探查」**：`text` 为 0 说明根组件没挂上；根组件是 `MainShell`（ErrorBoundary），组件炸了会显示异常正文而不是白屏。
- **组件依赖要同步 `MeetingHost.BuildServices`**：`MeetingResult` 注入的 `TranscriptEditor` 漏注册时，窗口能起来但页面全黑（组件构造抛异常，WebView 停在空白）。加新组件依赖先查这里。
- **WebView2 事件钩子放 `Loaded`**：WPF 的 `BlazorWebView.WebView` 在构造期是 null，ctor 里挂 `CoreWebView2InitializationCompleted` 会 NRE。GUI 没有控制台，排错看 `<数据根>\meeting.log`。
- **GUI 第二个实例静默退出**：不弹框，避免卡住 Hub 的自动拉起；排查时看日志里的「已有实例在运行」。
- 测试工程是 `net10.0`，**不能引用 `net10.0-windows` 的 Audio 工程**；采集要验就走 `IAudioCaptureFactory` 假实现（见 `RecordingTests`），真设备只能人工点一次。
- **录音失败别留空会议**：`CreateMeeting` 在 `capture.Start` 之前，设备起不来时要把刚建的会议软删，否则列表里会多一条"未转写"的幽灵记录。

## 不要读

`obj/`、`bin/`、`TestResults/`、`models/`、音频样本；`docs/requirements.md` 只通读一次。

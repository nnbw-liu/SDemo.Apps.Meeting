# AGENTS · sdemo.meeting 子仓

> 路由文件。跨产品文档在 `../../docs/`（相对本仓根）。

## 职责

录音、本地转写、议题/决议/候选、分析履历；会议横切多个项目，不隶属任何项目。

## 现状（2026-11-16）

✅ **阶段 A 已落地**：8 个工程 + `SDemo.Meeting.slnf`，`import → transcribe → list/show` 端到端跑通。
✅ **阶段 B 全部完成（B1–B7）**：VAD 分段（含窗口打包）、增量落库/取消/续转、模型库（打包 + 导入 + 校验 + 清理）、低配档自动回落、hotwords、语言 hint + 质检告警、转写结果页（WPF + BlazorWebView）。
A5 双路采集代码完成，但本机被 Windows 麦克风隐私拒绝（0x80070005），需在有权限的机器上复验。

| 层 | 状态 |
| --- | --- |
| `Contracts` | 枚举 / DTO / `IAsrProvider` 能力契约 / 错误码 / UUIDv7 / WAV 头解析 |
| `Storage` | `mtg_meeting` / `mtg_audio_asset` / `mtg_transcript_run` / `mtg_segment` / `mtg_meta`，schema v1 |
| `Core` | `MeetingService` 状态机、`TranscriptionRunner` 编排、`TranscriptQuality` 质检信号 |
| `Asr` | `WhisperCppProvider`（Whisper.net 1.8.1）、`ChatAudioProvider`（回环/云端 chat）、`DeepSeekToolsProvider`（`/v1/tools/asr`）、模型目录 |
| `Audio` | `DualTrackRecorder`（WASAPI 麦克风 + 环回，带启动超时看门狗）、`WavDownmixer` |
| `Mcp` | 4 个只读工具目录（server 待后续阶段） |
| `Web` | 结果页 Razor 组件 `MeetingResult.razor`（列表 / 段落 / 时间码 / 说话人标注） |
| `Viewer` | WPF + BlazorWebView 外壳（进程内组件，不起 HTTP）；`SDemo.Meeting.Viewer.exe` |
| `Host` | CLI：`record` / `import` / `transcribe` / `resume` / `list` / `show` / `models` / `doctor` / `tools` |

## 开工先做三件事

1. **阶段 A / B 都完成了**（[asr-runtime.md](docs/asr-runtime.md)）。下一步：MCP server（工具目录已就位，JSON-RPC 还没接）、议题/决议/候选三类表（`mtg_topic` / `mtg_decision` / `mtg_action`）、会议分析交给 Agent 的批处理链路。
2. Host 仍是 CLI；结果页是独立的 `SDemo.Meeting.Viewer.exe`（WPF + BlazorWebView）。
3. `workspaces/meeting.json` 已是 `active`。

模型侧：默认档由安装包自带（`tools/pack-models.ps1`），用户在 `meeting models` 里能看到状态、导入自己的模型、校验与清理。

## 必读

| 改什么 | 读 |
| --- | --- |
| 需求与验收 | [docs/requirements.md](docs/requirements.md) |
| 会议领域模型 | [docs/meeting-design.md](docs/meeting-design.md) |
| **ASR 运行时与开发计划（v0.4，含实测性能表与阶段进度）** | [docs/asr-runtime.md](docs/asr-runtime.md) |
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

# 转写结果页（WPF + BlazorWebView）
src\SDemo.App.Meeting.Viewer\bin\Release\net10.0-windows10.0.19041.0\SDemo.Meeting.Viewer.exe --data-dir=<数据根>
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
- 测试工程是 `net10.0`，**不能引用 `net10.0-windows` 的 Audio 工程**；采集只能靠 CLI 人工验。

## 不要读

`obj/`、`bin/`、`TestResults/`、`models/`、音频样本；`docs/requirements.md` 只通读一次。

# 主程序（应用外壳）实施计划

> 状态：**P0 进行中（2026-11-16）**
> 范围：把 sdemo.meeting 从"数据管线 + 只读查看器"做成"能被 Hub 安装、打开就能用"的应用。
> 关联：[meeting-design.md](meeting-design.md) · [asr-runtime.md](asr-runtime.md) · [ui-mockups/](ui-mockups/README.md) · [hub-protocol](../../../docs/contracts/hub-protocol.md)

---

## 0. 一句话

**数据面已经做完，缺的是那个"双击就能打开"的 exe。**

---

## 1. 现状盘点（2026-11-16）

| 已有 | 位置 |
| --- | --- |
| 会议 / 音频 / 转写 run / 段（schema v3） | `Storage` |
| VAD 分段、增量落库、取消/续转 | `Core.TranscriptionRunner` |
| whisper / chat-audio / deepseek-tools 三引擎、模型库、降档 | `Asr` |
| 双轨采集（未真机验证） | `Audio` |
| 议题 / 决议 / 候选 + 审计 | `Core.MeetingDomainService` / `MeetingAuditLog` |
| MCP 7 读 + 6 写 | `Mcp` + `Host.McpHttpServer` |
| 只读结果页（段落 / 时间码 / 标说话人） | `Web` + `Viewer` |
| CLI 12 个命令 | `Host` |

**缺的**：

1. 主窗口（侧栏 + 会议列表 + 详情 + 录音入口）；
2. 录音 UI（现在只有 `meeting record --seconds 30`）；
3. 议题 / 决议 / 候选的编辑界面（数据能写，但只有 Agent 和脚本能写）；
4. KB 投影界面（"确认并归档"）；
5. Hub 集成：`deploy/app.json`、单实例、空闲退出、托盘；
6. 首启引导（模型未下载时）。

---

## 2. 一个必须先拍的架构决定：单进程

**现状**：`SDemo.Meeting.exe`（CLI，`serve` 时起 MCP）与 `SDemo.Meeting.Viewer.exe`（WPF 结果页）是两个进程，共用同一个 `meeting.db`。

**问题**：

| 问题 | 后果 |
| --- | --- |
| 两个进程各自 `MeetingDatabase.Open` | 并发迁移会撞；谁先起谁拿库 |
| MCP 生命周期与 UI 分离 | 关掉界面 Agent 还能写；界面开着但没 `serve`，Agent 找不到它 |
| 用户要记住"先 serve 再 viewer" | 不可接受 |

**决定**：合并成一个 `SDemo.Meeting.exe`（Planner 同款）：

```
SDemo.Meeting.exe
  ├─ WPF 外壳 + BlazorWebView（组件仍然在 Web 项目里）
  ├─ 内嵌 McpHttpServer（进程内起 /mcp，写 runtime 文件）
  └─ --headless <cmd> 走 CLI（脚本 / CI / 调试用，不弹窗）
```

代价：Host 从 `Microsoft.NET.Sdk`（控制台）变成 `Microsoft.NET.Sdk.Razor` + `UseWPF` + `WinExe`；`Program.cs` 改成 `[STAThread]` 双模入口；`Viewer` 工程并入 `Host` 后删除。

---

## 3. 分期

### P0 · 主程序骨架（1 周）✅ 2026-11-16 完成

| # | 任务 | 验收 | 状态 |
| --- | --- | --- | --- |
| P0-1 | Host 改 Razor + UseWPF + WinExe；`Viewer` 并入后删除 | 构建 0 警告 | ✅ |
| P0-2 | 双模入口：无参 → GUI；带参 → CLI（`AttachConsole` 接父控制台） | 双击出窗口；`meeting.exe list` 有输出 | ✅ |
| P0-3 | 进程内起 MCP + 写 runtime 文件 | 窗口开着时 `state=running`、`/v1/health` 200 | ✅ |
| P0-4 | 单实例 mutex + 关窗即停服务 | 第二个实例静默退出（不弹框，免得卡住 Hub 拉起）；关窗后转 `stopped` | ✅ |
| P0-5 | `deploy/app.json`（Hub 安装清单） | 字段与 Planner 对齐；`contract-lint` 自身仍坏（`DesktopAssembly/samples/hello-app` 缺失），未能校验 | ⚠️ |

**落地细节**

- 入口：`meeting.exe` 无参 → GUI；`meeting.exe --gui [--instance x] [--data-dir y] [--port n]` 显式指参数；其它任何参数 → CLI。
- 组合根 `MeetingHost` 同时被 GUI 与 CLI 使用，**同一个进程、同一个库**，不再有"界面和 CLI 抢库"的问题。
- 日志：`<数据根>\meeting.log`（启动、MCP 端口、WebView 导航结果、退出码）。GUI 没有控制台，出问题只能靠它。
- 关窗 → `host.Dispose()` → `McpHttpServer.Stop()` → runtime 文件转 `stopped`。

**踩到的坑**

| 现象 | 原因 | 处理 |
| --- | --- | --- |
| 窗口起来但内容全黑 | `MeetingResult` 注入的 `TranscriptEditor` 没在 DI 注册，组件构造就抛异常，页面停在空白 | 注册 `TranscriptEditor`；以后加组件依赖记得同步 `MeetingHost.BuildServices` |
| 拿不到 WebView2 事件 | WPF 的 `BlazorWebView.WebView` 在 ctor 里还是 null | 钩子挪到 `Loaded`，并做空判断 |
| 空 POST 到 `/v1/shutdown` 回 411 | `HttpListener` 要求 `Content-Length` | 客户端带 `Content-Length: 0`（curl 用 `-d ''`） |

### P1 · 录音（1 周）🟡 2026-11-16 主体完成

| # | 任务 | 验收 | 状态 |
| --- | --- | --- | --- |
| P1-1 | 主界面「开始录音」+ 计时 + 结束 | 点一下开录，再点结束 | ✅ 代码就位；本机麦克风被拒，只验到失败分支 |
| P1-2 | 双轨选择（麦克风 / 环回）+ 设备不可用时的可读提示 | 无权限时提示去设置，不崩不挂 | ✅ 实机验证（截图见下） |
| P1-3 | 录音后自动转写 + 进度条 + 取消 | 界面能看到进度；取消保留已完成段 | ✅ 状态机有 9 个测试覆盖；真机待麦克风权限 |
| P1-4 | 桌面录音小组件（mockup 06，半透明置顶） | 录音时出现，结束后消失 | ⬜ 推迟到 P2 之后（要单开一个 Window，先把主流程坐实） |

**落地细节**

- 状态机在 `Core.RecordingCoordinator`（net10.0），采集与 ASR 都是注入的：
  `IAudioCaptureFactory` 由 Host 用 `DualTrackRecorder` 实现，`Func<IAsrProvider>` 用 whisper（带降档）。
  这样没有麦克风的机器也能用假采集把「开始 → 停止 → 自动转写」跑完（`RecordingTests`）。
- UI 是 `Web/RecordPanel.razor`：录音卡在侧栏「● 录音」里展开；没有会议时默认展开。
- 计时用 `Timer` 每秒推快照，界面不做假动画。
- 停止后自动挂 `audio_asset` 并转写，进度以 `AsrProgress` 推给进度条。

**踩到的坑**

| 现象 | 原因 | 处理 |
| --- | --- | --- |
| 失败后在列表里留下一条「未转写」的空会议 | `CreateMeeting` 先于 `capture.Start`，设备起不来时会议已经建好了 | 启动失败时软删刚建的会议；`RecordingTests.设备起不来不会留下空会议` 守住 |
| 0x80070005 直接甩给用户 | 设备层抛的是 Win32 码 | `DescribeRecordingFailure` 翻译成「去设置里允许麦克风」，错误码仍是 `MTG_NO_AUDIO_DEVICE` |

**本机阻塞**：Windows 麦克风隐私拒绝（`0x80070005`）。放开路径：
`设置 → 隐私和安全性 → 麦克风 → 允许桌面应用访问麦克风`。放开后 `dotnet test` 里的真实采集仍不覆盖，
需要人工点一次「开始录音 → 停止并转写」。

### P2 · 议题 / 决议 / 候选编辑（1.5 周）🟡 2026-11-16 主体完成

| # | 任务 | 验收 | 状态 |
| --- | --- | --- | --- |
| P2-1 | 会议详情三栏（时间轴 / 转写 / 检视栏），对齐 mockup 02 | 能看到议题列表与计数 | ✅ 右栏落地（时间轴仍是时间码按钮，未做横向时间轴） |
| P2-2 | 段落"标为决议"/"标为候选"，带 anchor（时间码） | 从转写直接生成决议/候选 | ✅ 实机点过：决议带 `00:00:00` anchor 进右栏 |
| P2-3 | 议题增删改、refs 编辑、确认决议、候选三态 | 不碰命令行能走完一场会 | 🟡 议题加/删、确认决议、候选拒绝已通；**改名与 refs 编辑未做**；`intaken` 需 plannerRef，界面暂只提供"不做" |
| P2-4 | 结果页显示审计（谁改了什么） | `meeting audit` 的内容能在界面里看到 | ✅ 右栏「最近改动」；**UI 写操作也落审计**（actor=User） |

**落地细节**

- `Web/DomainPanel.razor`：右栏（议题 / 决议 / 候选 / 最近改动），只做展示与按钮，写操作调 `MeetingDomainService`。
- `MeetingResult.razor`：段落多两个按钮「标为决议 / 标为候选」，按**当前选中议题**归档并带 `anchorMs`。
- 未选议题时也能标（topicId 为空），决议/候选不强制挂议题。
- UI 写操作与 MCP 写操作**都落 `mtg_audit`**，actor 分别是 `User` / `Agent`。

**踩到的坑（重要）**

| 现象 | 原因 | 处理 |
| --- | --- | --- |
| 加了右栏后窗口一片空白，日志没有异常、页面探查也不返回 | `DomainPanel` 在 `OnParametersSet` 里回调父组件（`OnSelectTopic.InvokeAsync`）改父状态 → 参数设置期间的重入渲染，渲染器直接卡死（不是抛异常，是自杀式静默） | **不要在 `OnParametersSet` 里调用父组件回调**；改成只在用户点击时触发 |
| 空白窗口难定位 | GUI 没有控制台，Blazor 渲染器死了不会留下任何痕迹 | `meeting.log` 里有「导航完成 / 页面探查 `{text, app}`」：`text` 为 0 说明根组件没挂上 |
| 组件异常导致整页空白 | 没有错误边界 | 根组件换成 `MainShell.razor`（`<ErrorBoundary>`），炸了显示异常正文 |
| WebView2 偶发只画背景色 | 多实例共用 user data folder / 残留锁 | 每个实例用 `<数据根>\webview2`；实在不行设 `SDEMO_MEETING_DISABLE_GPU=1` |

### P3 · 知识库投影（1 周）🟡 2026-11-16 写入链路完成

| 任务 | 验收 | 状态 |
| --- | --- | --- |
| 「确认并归档」流程 | 结论 + 转写一起进 KB | ✅ 头部「归档到知识库」：先 `ingest`（含转写）再 `promote` 到 durable |
| 投影状态可见 | 列表行 / 详情 / 同步条三处一致 | 🟡 详情页有状态 pill（未归档 / 投影中 / 已归档 / 归档失败）；列表行徽章未做 |
| outbox 与 `/v1/ingest` + `/v1/promote` | KB 离线不阻塞会议 | ✅ 失败只记 `Failed` + 次数，启动时补投（次数 <5）；KB 不在时给可照做的提示 |

**落地细节**

- `Core/KbProjectionService`：把会议变成 `kind=decision` / `retention=staging` 的 ingest 文档
  （会议头 + 议题 + 决议 + 候选 + 可选转写），归档时再 `promote` 成 `durable`。
- **幂等**：`idempotencyKey = sdemo.meeting/<id>/sha256:<正文前 16 字节>`，同样内容重复投影不会在 KB 里长第二份。
- `Core/KbConnection`：地址读 `%LOCALAPPDATA%\SDemo\Hub\apps\sdemo.kb.json`，token 读
  `...\sdemo.kb\tokens\sdemo.meeting.token`；两者缺一就当 KB 不在。
- `Core/HttpKbTransport`：只做 POST + 解 `docId/version`，HTTP 失败也返回 `KbIngestResult(Error)` 而不是抛。
- 传输抽象成 `IKbTransport`，所以整条投影逻辑在没有 KB 的机器上有 8 个测试覆盖。

**与 KB 契约的一处偏差（需要你知道）**

`kb-design` §4.2 写的是「会议原始转写不进 KB」（噪音 + 隐私 + 体积）。但你的产品决定是
**证据和结论都进 KB**（会议里删了还能去 KB 查）。折中：

- 默认投影（自动/后台）只写结论：议题、决议、候选；
- 只有用户点「归档到知识库」这个显式动作，才把转写一起写进去。

这样契约默认仍然成立，用户显式要求时才有证据链。

**没做**

- 列表行的 KB 徽章；`/v1/refs` 登记（议题 refs 目前只是写进正文，没登记成引用）；
- KB 侧 `kb_block.origin`（从 KB 跳回会议原文）需要先提契约。
### P4 · 分析链路（1 周，Agent 侧为主）🟡 2026-11-16 Meeting 侧完成

| 任务 | 验收 | 状态 |
| --- | --- | --- |
| Meeting 侧分析队列 | Agent 能按作业取件 | ✅ `mtg_analysis` + `meeting_request/claim/finish/status` 四个工具；claim 原子、入队幂等 |
| 分段取件 | Agent 按窗口拉段 | ✅ 复用 `meeting_transcript` 的 `fromMs/toMs/limit` |
| Agent 侧 map-reduce | 点「分析」后界面出现待审阅条目 | ⬜ **未做**（在 DesktopAgent：claim → 逐段抽 → 写工具落库 → finish） |
| 履历 | provider / prompt_version 可追溯 | 🟡 作业行记了 provider / promptVersion / 段数 / 产出条数 |

**分工（重要）**

Meeting **不调模型**（零凭证铁律）。所以 P4 被切成两半：

```
Meeting（已完成）                 DesktopAgent（待做）
用户点「分析」→ 入队    ──►   claim → 分页取段 → 逐段抽 → 写工具 → finish
界面轮询状态 / 产出条数 ◄──
```

**Meeting 侧约定**

- 同一场会议同时只允许一个 `Queued`/`Running`，重复点「分析」幂等返回同一个作业。
- `claim` 用带 `status='Queued'` 条件的 UPDATE，两个 Agent 抢同一场只会有一个人拿到。
- 完成时**只记产出条数**，真正的议题/决议/候选仍走 P2 的写工具——写路径只有一条。
- 失败记 `error` 后可重新入队（会新建一个作业，旧作业留作履历）。
- 没有转写结果的会议不能入队（直接报错，不给 Agent 空作业）。

**实测（MCP 驱动，模拟 Agent）**

```
request  → queued, segmentsTotal=9
claim    → running
finish   → done, topics=2 decisions=1 actions=3 provider=deepseek-flash
status   → done
审计     → request / claim / finish 三条 Agent 记录（失败的一次也留了 error 行）
```

界面：会议头显示 `已分析 · 2 议题`，点「分析」后转 `待分析` → `分析中 n/9` → 完成后刷新右栏。
--- | --- |
| Meeting 侧 `mtg_analysis_request` 队列 + 分段取件端点 | Agent 能按窗口拉段 |
| Agent 侧 map-reduce：逐段抽议题/决议/候选 → reduce 汇总 | 点"分析"后界面出现待审阅条目 |
| `mtg_analysis` 履历（provider / prompt_version） | 重跑可对比 |

---

## 4. 不做的事

| 不做 | 理由 |
| --- | --- |
| 托盘常驻 + 开机自启 | v1 先单窗口；常驻交给 Hub 的 idle 机制 |
| 多人协同编辑纪要 | 单机产品 |
| 桌面组件（除录音小组件） | 宿主组件契约要求 `widgets` 数组 + Hub 目录，等主窗口稳定再说 |
| 在线更新 | 由 Hub 的安装/升级流程负责 |

---

## 5. 进度记录

| 日期 | 事件 |
| --- | --- |
| 2026-11-16 | 阶段 A/B、领域模型、MCP 读写面完成；本计划立稿 |
| 2026-11-16 | **P0 完成**：单进程（WPF 外壳 + 进程内 MCP）、双模入口、单实例、`deploy/app.json`、`meeting.log`；实机截图确认窗口渲染、关窗后 runtime 转 `stopped` |
| 2026-11-16 | **P1 主体完成**：录音卡 + 计时 + 双轨开关 + 停止自动转写 + 进度 + 取消；状态机抽到 Core 并用假采集写了 9 个测试；P1-4 桌面小组件推迟 |
| 2026-11-16 | **P4 Meeting 侧完成**：分析队列 + 4 个 MCP 工具 + 界面状态轮询；Agent 侧 map-reduce 驱动未做 |
| 2026-11-16 | **P3 写入链路完成**：会议 → KB 的 ingest/promote、幂等键、离线补投、状态 pill；KB 未装时实机验证了失败提示 |
| 2026-11-16 | **P2 主体完成**：三栏 + 右栏检视（议题/决议/候选/审计）+ 段落「标为决议/候选」带 anchor + 确认决议；踩了 `OnParametersSet` 重入导致渲染器静默死亡的坑，加了错误边界与页面探查日志 |

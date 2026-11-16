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

### P1 · 录音（1 周）

| # | 任务 | 验收 |
| --- | --- | --- |
| P1-1 | 主界面「开始录音」+ 计时 + 结束 | 点一下开录，再点结束 |
| P1-2 | 双轨选择（麦克风 / 环回）+ 设备不可用时的可读提示 | 无麦克风权限时提示去设置，不崩不挂 |
| P1-3 | 录音后自动转写 + 进度条 + 取消 | 界面里能看到进度；取消保留已完成段 |
| P1-4 | 桌面录音小组件（mockup 06，半透明置顶） | 录音时出现，结束后消失 |

**阻塞**：本机 Windows 麦克风权限被拒（`0x80070005`）。代码就绪后需要用户放开权限才能真机验收。

### P2 · 议题 / 决议 / 候选编辑（1.5 周）

| # | 任务 | 验收 |
| --- | --- | --- |
| P2-1 | 会议详情三栏（时间轴 / 转写 / 检视栏），对齐 mockup 02 | 能看到议题列表与计数 |
| P2-2 | 段落"标为决议"/"标为候选"，带 anchor（时间码） | 从转写直接生成决议/候选 |
| P2-3 | 议题增删改、refs 编辑、确认决议、候选三态 | 不碰命令行能走完一场会 |
| P2-4 | 结果页显示审计（谁改了什么） | `meeting audit` 的内容能在界面里看到 |

### P3 · 知识库投影（1 周）

| 任务 | 验收 |
| --- | --- |
| 「确认并归档」流程（mockup 03）：结论 + 转写一起进 KB | 归档后 KB 能搜到 |
| 投影状态可见（v3 / 块数 / 排队中） | 列表行、详情右栏、同步条三处一致 |
| outbox 与 `/v1/ingest` + `/v1/promote` | KB 离线不阻塞会议 |

### P4 · 分析链路（1 周，Agent 侧为主）

| 任务 | 验收 |
| --- | --- |
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

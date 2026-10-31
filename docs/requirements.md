# sdemo.meeting 需求计划

> 状态：需求计划（v1），2026-10-31
> 范围：需求编号 MT-01–MT-14；分期见 §5。
> 关联：[品牌蓝图](../../../docs/sdemo-brand-blueprint.md) §D · [会议设计](meeting-design.md) · [联动需求](../../../docs/integration.md)

---

## 1. 目标与边界

**一句话**：录音、转写、议题、决议、待办候选与分析履历，全部本地保存。

- 会议**不隶属任何项目**：用 `mtg_topic` 拆分议题，每个议题各自挂 `sdemo.kb/...`、`sdemo.planner/...` URI。
- **候选 ≠ 待办**：会议只产出 `mtg_action(candidate)`；转待办要经 DesktopAgent 中转并由用户在 planner 审阅。
- **转写走本地 whisper**：音频不出本机；云端 ASR 只能是用户显式开启的例外。
- **零模型凭证**：分析由 DesktopAgent 调用，本应用只存 `model` + `prompt_version` 与结果。
- 数据目录：`%APPDATA%\SDemo\Apps\sdemo.meeting\`（`meeting.db` + `meetings\{id}\audio\`）。

**v1 不做**：实时字幕、自动说话人分离、云端默认转写、多人协同编辑纪要。

## 2. 需求清单

| # | 需求 | 优先级 | 验收要点 |
| --- | --- | --- | --- |
| MT-01 | 采集两种模式：`room`（麦克风）/ `online`（环回 + 麦克风） | P0 | 双路分别转写后按时间合并 |
| MT-02 | 录音存应用数据目录，可导出（m4a/mp3） | P0 | 目录可见、可备份、可清理 |
| MT-03 | 本地 whisper 转写（不出网），模型档位可选 | P0 | 音频零上传；后台可取消 |
| MT-04 | 转写片段：起止毫秒 + 文本 + 置信度 | P0 | 点击跳回录音位置 |
| MT-05 | 说话人：v1 手动标注，**预留自动分离接口** | P0 | 字段 `speaker` + `speaker_source(manual/auto)`；`IDiarizationProvider` 就位 |
| MT-06 | 议题（topic）拆分 + 每议题挂 `refs`（跨项目 URI） | P0 | 一场周会可挂 3 个项目 |
| MT-07 | 决议 `decision`（proposed / confirmed） | P0 | 只有 confirmed 进正式纪要 |
| MT-08 | 待办候选 `action`（candidate / intaken / rejected） | P0 | **不直接创建待办** |
| MT-09 | 分析存档：`model` + `prompt_version`，可重跑并存多版 | P0 | 能对比两次分析差异 |
| MT-10 | 导出 Markdown / JSON 纪要 | P1 | 排版可读 |
| MT-11 | MCP 工具面 `meeting_*` | P0 | `refs` 写入校验存在性 |
| MT-12 | 录音保留策略（90 天 / 永久），占用可见 | P1 | 自动清理前提示 |
| MT-13 | 桌面录音组件（计时 + 结束按钮） | P1 | 半透明、置顶、不抢焦点 |
| MT-14 | 自动说话人分离（diarization 模型接入） | P2 | 接上 `IDiarizationProvider`，`speaker_source=auto` |

## 3. 数据与流程

| 项 | 约定 |
| --- | --- |
| 核心表 | `mtg_meeting` / `mtg_topic` / `mtg_segment` / `mtg_decision` / `mtg_action` / `mtg_analysis` / `mtg_ref` / `audio_asset` |
| 采集 | `room` 单路麦克风；`online` 环回 + 麦克风双路，分别转写后按时间戳合并 |
| 转写 | whisper 本地；片段持久化毫秒起止 + 置信度；模型档位 small / medium 对用户可见 |
| 议题 | 会议 → 议题 → 决议/候选；`refs` 存 URI，写入时校验存在性（调 Agent 或读注册表） |
| 分析 | 每次分析存 `model` / `prompt_version` / 输入快照 hash；可重跑并保留多版 |
| 保留 | 录音默认 90 天，可设永久；清理前提示并显示占用 |
| 导出 | Markdown / JSON；音频 m4a / mp3 |

## 4. 工程约束（多仓）

- 五件套 + `Asr` / `Audio`：`SDemo.App.Meeting.{Contracts,Storage,Core,Mcp,Host}`、`Asr`、`Audio`。
- 不引用 Agent、不引用其它 App；候选转待办由 Agent 调 planner 的 `todo_create_draft`。
- 工具面：`meeting_list` / `meeting_get` / `meeting_start` / `meeting_stop` / `meeting_transcribe` / `meeting_topics` / `meeting_decisions` / `meeting_actions` / `meeting_analyze` / `meeting_export` / `meeting_refs`；写操作声明风险等级。
- 模型文件不入库，首次运行按需下载 + sha256 校验。

## 5. 分期与交付

| 期 | 覆盖需求 | 交付物 | 验收 |
| --- | --- | --- | --- |
| **P6** v1 | MT-01 ~ MT-09、MT-11、MT-13 | 录音、本地转写、议题/决议/候选、`meeting_*` MCP、录音组件 | 30 分钟会议能产出可审阅的待办候选；跨项目议题各自挂到对应文档 |
| **P6 尾** | MT-10、MT-12 | 纪要导出、保留策略 | 导出可读；占用可见 |
| **P6+** | MT-14 | 自动说话人分离 | `speaker_source=auto`，手动可改 |

## 6. 联动点

| 联动 | 做法 |
| --- | --- |
| 候选 → 待办 | 用户选中 `mtg_action(candidate)` → Agent 调 planner `todo_create_draft` → 审阅入库；Meeting 侧状态置 `intaken` |
| 议题挂文档 | `mtg_topic.refs` 存 `sdemo.kb/doc/<id>@v#b`；Agent 解析跳转 |
| 统一搜索 | Agent fan-out 调 `meeting_list` / 片段检索，结果合并 |
| 分析重跑 | Agent 调 `meeting_analyze`，传 `model` / `prompt_version`；结果存多版可对比 |

## 7. 风险与待定

| 项 | 说明 |
| --- | --- |
| R1 | 本地转写耗时（1h ≈ 12–60 分钟）与中文质量 → 后台任务可取消、档位可选、质量不足再评估云端例外 |
| R2 | 远端录音拾音差 → `online` 环回 + 麦克风双路，界面提示设备选择 |
| R3 | 磁盘占用（音频 + 模型）→ 保留策略 + 占用可见 + 模型按需下载 |
| Q1 | 云端 ASR 是否作为显式例外提供 |
| Q2 | 会议类型枚举（周会 / 客户沟通 / 复盘 / 其它）是否够用 |
| Q3 | whisper 模型档位默认（建议 small，可切 medium） |

# SDemo.Apps.Meeting

**sdemo.meeting**：录音、本地转写、议题、决议、待办候选与分析履历，全部本地保存。

> 本仓是 [SDemo 主仓](https://github.com/nnbw-liu/SDemo) 的子模块（`Apps/Meeting`）。
> 状态：🚧 **规划中（P6）**，本仓目前只有文档，没有代码。

## 文档

| 想知道 | 读 |
| --- | --- |
| 需求计划（MT-01–14、分期、验收） | [docs/requirements.md](docs/requirements.md) |
| 会议模型、议题/决议/候选、分析存档 | [docs/meeting-design.md](docs/meeting-design.md) |
| 需求原文与优先级 | [../../docs/sdemo-brand-blueprint.md](../../docs/sdemo-brand-blueprint.md) §D |
| 联动（候选 → 待办、统一搜索） | [../../docs/integration.md](../../docs/integration.md) |

## 边界

- **会议不隶属任何项目**：用 `mtg_topic` 拆议题，各自挂跨项目 URI。
- **候选 ≠ 待办**：只产出 `mtg_action(candidate)`，转待办经 DesktopAgent 编排 + 用户审阅。
- **转写全本地**：whisper 本地运行，音频零上传；模型文件不入库。
- **零模型凭证**：分析由 DesktopAgent 调用；本应用存 `model` + `prompt_version` 以便重跑。
- 数据目录 `%APPDATA%\SDemo\Apps\sdemo.meeting\`（`meeting.db` + `meetings\{id}\audio\`）。

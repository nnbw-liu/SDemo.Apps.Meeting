# AGENTS · sdemo.meeting 子仓

> 路由文件。跨产品文档在 `../../docs/`（相对本仓根）。

## 职责

录音、本地转写、议题/决议/候选、分析履历；会议横切多个项目，不隶属任何项目。

## 现状（2026-10-31）

🚧 未开工：只有文档，`src/` 还不存在。需求计划见 [docs/requirements.md](docs/requirements.md)。

## 开工先做三件事

1. 先补 `docs/asr-runtime.md`（whisper 模型分发、校验、耗时档位）——这是本产品最大的未知。
2. 按五件套建工程：`src/SDemo.App.Meeting.{Contracts,Storage,Core,Mcp,Host}`（另加 `Asr` / `Audio`）。
3. 建 `SDemo.Meeting.slnf`；在父仓 `workspaces/meeting.json` 把 `status` 改 `active`。

## 必读

| 改什么 | 读 |
| --- | --- |
| 需求与验收 | [docs/requirements.md](docs/requirements.md) |
| 会议领域模型 | [docs/meeting-design.md](docs/meeting-design.md) |
| 跨应用联动（候选转待办） | [../../docs/integration.md](../../docs/integration.md) §3 |
| 组件与宿主 | [../../docs/contracts/](../../docs/contracts/) |

## 命令（建好工程后启用）

```powershell
dotnet build SDemo.Meeting.slnf -c Release
dotnet test  tests -c Release
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

## 不要读

`obj/`、`bin/`、`TestResults/`、`models/`、音频样本；`docs/requirements.md` 只通读一次。

# sdemo.meeting 界面原型

静态 HTML 原型 + 截图，用来看设计，不是可运行的应用界面。
设计系统与 sdemo.kb 原型同源：`sdemo-ui.css` 是从 `Shared/UI/SDemo.UI.Blazor/wwwroot/css/` 拷来的**只读副本**。

## 文件

| 文件 | 说明 |
| --- | --- |
| `build.py` | 原型生成器（唯一需要手改的地方是它和 `meeting.css`） |
| `meeting.css` | 会议特有的版式补充（时间轴、转写流、投影状态、录音组件） |
| `sdemo-ui.css` | **只读副本**，来自 `Shared/UI/SDemo.UI.Blazor/wwwroot/css/sdemo-ui.css` |
| `01-meetings.html` / `.png` | 全部会议：进行中 / 待确认 / 已归档，每行带知识库投影状态 |
| `02-meeting.html` / `.png` | 会议详情：时间轴 · 转写流 · 议题/候选/知识库投影 |
| `02-meeting-light.html` / `.png` | 同上，浅色 |
| `03-finalize.html` / `.png` | 确认并归档：一次确认，结论与证据一起进知识库 |
| `04-actions.html` / `.png` | 待办候选 → 交给 Agent 编排（审批卡 + 状态回流） |
| `05-kb.html` / `.png` | 知识库侧：会议文档长什么样（时间码可跳回录音） |
| `06-widget.html` / `.png` | 桌面录音组件（半透明置顶） |

## 这几屏编码了几个决定

1. **写口只有一个出口**：会议 → 知识库，单向、可离线补投。界面上从不出现"发送到 Planner"。
2. **结论与证据一起归档**：确认时打包「结论 4 块 + 转写 134 段 + 录音原件」，转写带时间码，进知识库后仍可逐段定位。
3. **归档即解耦**：知识库副本独立于本应用。在本应用删除会议，知识库那份还在（03 里的那行 callout 是给用户看的承诺，也是实现约束）。
4. **候选不是待办**：04 的 Agent 审批卡是唯一出口；回流也由 Agent 写 `plannerRef`，会议不轮询 Planner。
5. **投影状态是一等公民**：列表行、详情右栏、底部同步条三处都能看到 `v3 / 块数 / 排队中`，不是藏在设置里。
6. **知识库离线不阻塞会议**：01 里那行「2 次更新排队中」就是 outbox 的可视化。

## 重新生成

```powershell
cd Apps\Meeting\docs\ui-mockups
python build.py
```

## 重新截图

Chrome 无头模式，视口 1440×900；每个页面用独立 profile，避免复用实例导致漏截：

```powershell
$chrome = "C:\Program Files\Google\Chrome\Application\chrome.exe"
$dir = (Resolve-Path .).Path -replace '\\','/'
foreach ($p in "01-meetings","02-meeting","02-meeting-light","03-finalize","04-actions","05-kb","06-widget") {
  $args = @("--headless=new","--disable-gpu","--hide-scrollbars","--no-first-run",
            "--user-data-dir=$env:TEMP/mtg-$p","--force-device-scale-factor=1",
            "--window-size=1440,900","--virtual-time-budget=3000",
            "--screenshot=$dir/$p.png","file:///$dir/$p.html")
  Start-Process -FilePath $chrome -ArgumentList $args -Wait -NoNewWindow
}
```

## 约束

1. **不要在这里改设计 token**。要改就改 `Shared` 子仓并升版本，然后重新拷 `sdemo-ui.css` 过来。
2. `meeting.css` 里只允许用 `sdemo-ui.css` 定义的 `--*` 变量；浅色主题用 `[data-theme="light"]` 覆盖。
   目前有一处例外：`sdemo-ui.css` 把 `.nav-item.active` 的颜色写死成 `#fff`、`.status-pill.is-success` 写死成 `#7ee2b0`，
   浅色下不可用；`meeting.css` 末尾就地覆盖了这两条。**根因应在 Shared 修**，修完把覆盖删掉。
3. 原型只表达**版式与状态**，交互是死的；真实实现走 Blazor + 设计系统的 `AppShell` / `Modal` / `SelectBox` / `ToastHost`。
4. 06 是桌面组件，不是应用窗口：它走 DesktopAssembly 的 `native` 组件机制，配色不跟随应用主题。

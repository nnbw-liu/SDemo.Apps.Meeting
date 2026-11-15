#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
sdemo.meeting 界面原型生成器。

用法：
    python build.py

产出 ui-mockups/*.html。样式复用 SDemo.UI.Blazor 的设计系统
（sdemo-ui.css 是从 Shared 子仓拷贝的只读副本，别在这里改）。
改版式请改 meeting.css；改 token 请改 Shared 子仓并升版本。
"""

import pathlib

HERE = pathlib.Path(__file__).parent

# ── 图标：与 SDemo.UI.Blazor/Components/Icon.razor 同源 ──────────────────
ICONS = {
    "calendar": "<rect x='3' y='5' width='18' height='16' rx='2.5'/><path d='M8 3v4M16 3v4M3 10h18'/>",
    "file": "<path d='M14 3H6.5A2.5 2.5 0 0 0 4 5.5v13A2.5 2.5 0 0 0 6.5 21h11a2.5 2.5 0 0 0 2.5-2.5V9z'/><path d='M14 3v6h6'/><path d='M8 13h8M8 17h5'/>",
    "trash": "<path d='M4 7h16'/><path d='M9 7V4h6v3'/><path d='M6 7l1 13h10l1-13'/><path d='M10 11v5M14 11v5'/>",
    "plus": "<path d='M12 5v14M5 12h14'/>",
    "search": "<circle cx='11' cy='11' r='7'/><path d='m20 20-4-4'/>",
    "check": "<path d='m5 12 4.5 4.5L19 7'/>",
    "clock": "<circle cx='12' cy='12' r='9'/><path d='M12 7v5l3.5 2'/>",
    "target": "<circle cx='12' cy='12' r='9'/><circle cx='12' cy='12' r='5'/><circle cx='12' cy='12' r='1'/>",
    "spark": "<path d='M12 3l1.8 5.2L19 10l-5.2 1.8L12 17l-1.8-5.2L5 10l5.2-1.8z'/>",
    "chevron-right": "<path d='m9 5 7 7-7 7'/>",
    "chevron-down": "<path d='m5 9 7 7 7-7'/>",
    "arrow-right": "<path d='M4 12h16M13 5l7 7-7 7'/>",
    "x": "<path d='M6 6l12 12M18 6 6 18'/>",
    "edit": "<path d='M12 20h9'/><path d='M16.5 3.5a2.1 2.1 0 0 1 3 3L7 19l-4 1 1-4z'/>",
    "more": "<circle cx='5' cy='12' r='1'/><circle cx='12' cy='12' r='1'/><circle cx='19' cy='12' r='1'/>",
    "inbox": "<path d='M4 13h4l1.5 3h5L16 13h4'/><path d='M5 5h14l2 8v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4z'/>",
    "layers": "<path d='m12 3 9 5-9 5-9-5z'/><path d='m3 13 9 5 9-5'/><path d='m3 17 9 5 9-5'/>",
    "settings": "<circle cx='12' cy='12' r='3'/><path d='M19.4 15a1.7 1.7 0 0 0 .3 1.9l.1.1a2 2 0 1 1-2.8 2.8l-.1-.1a1.7 1.7 0 0 0-1.9-.3 1.7 1.7 0 0 0-1 1.5V21a2 2 0 1 1-4 0v-.1a1.7 1.7 0 0 0-1.1-1.5 1.7 1.7 0 0 0-1.9.3l-.1.1a2 2 0 1 1-2.8-2.8l.1-.1a1.7 1.7 0 0 0 .3-1.9 1.7 1.7 0 0 0-1.5-1H3a2 2 0 1 1 0-4h.1a1.7 1.7 0 0 0 1.5-1.1 1.7 1.7 0 0 0-.3-1.9l-.1-.1a2 2 0 1 1 2.8-2.8l.1.1a1.7 1.7 0 0 0 1.9.3H9a1.7 1.7 0 0 0 1-1.5V3a2 2 0 1 1 4 0v.1a1.7 1.7 0 0 0 1 1.5 1.7 1.7 0 0 0 1.9-.3l.1-.1a2 2 0 1 1 2.8 2.8l-.1.1a1.7 1.7 0 0 0-.3 1.9V9a1.7 1.7 0 0 0 1.5 1H21a2 2 0 1 1 0 4h-.1a1.7 1.7 0 0 0-1.5 1z'/>",
    "sun": "<circle cx='12' cy='12' r='4'/><path d='M12 2v2M12 20v2M4.9 4.9l1.4 1.4M17.7 17.7l1.4 1.4M2 12h2M20 12h2M4.9 19.1l1.4-1.4M17.7 6.3l1.4-1.4'/>",
    "moon": "<path d='M21 12.8A9 9 0 1 1 11.2 3a7 7 0 0 0 9.8 9.8z'/>",
    "info": "<circle cx='12' cy='12' r='9'/><path d='M12 11v5'/><circle cx='12' cy='7.8' r='.9'/>",
    "warning": "<path d='M12 4 21 20H3z'/><path d='M12 10v4'/><circle cx='12' cy='17.4' r='.9'/>",
    "link": "<path d='M10 13a4.5 4.5 0 0 0 6.4 0l2.6-2.6a4.5 4.5 0 0 0-6.4-6.4L11 5.6'/><path d='M14 11a4.5 4.5 0 0 0-6.4 0L5 13.6a4.5 4.5 0 0 0 6.4 6.4L13 18.4'/>",
    "external": "<path d='M14 4h6v6'/><path d='M20 4 11 13'/><path d='M19 14v4.5A2.5 2.5 0 0 1 16.5 21h-11A2.5 2.5 0 0 1 3 18.5v-11A2.5 2.5 0 0 1 5.5 5H10'/>",
    "download": "<path d='M12 3v12'/><path d='m7 10 5 5 5-5'/><path d='M4 20h16'/>",
    "refresh": "<path d='M20 12a8 8 0 1 1-2.3-5.7'/><path d='M20 4v5h-5'/>",
    "mic": "<rect x='9' y='3' width='6' height='11' rx='3'/><path d='M5 11a7 7 0 0 0 14 0'/><path d='M12 18v3'/>",
    "play": "<path d='M8 5.5v13l11-6.5z'/>",
    "pause": "<rect x='7' y='5' width='3.5' height='14' rx='1'/><rect x='13.5' y='5' width='3.5' height='14' rx='1'/>",
    "stop": "<rect x='6' y='6' width='12' height='12' rx='2'/>",
    "database": "<ellipse cx='12' cy='6' rx='7.5' ry='3'/><path d='M4.5 6v6c0 1.66 3.36 3 7.5 3s7.5-1.34 7.5-3V6'/><path d='M4.5 12v6c0 1.66 3.36 3 7.5 3s7.5-1.34 7.5-3v-6'/>",
    "users": "<circle cx='9' cy='8' r='3.2'/><path d='M3.5 20a5.5 5.5 0 0 1 11 0'/><path d='M16 5.2a3.2 3.2 0 0 1 0 6.1'/><path d='M17.5 14.2A5.5 5.5 0 0 1 21 20'/>",
    "send": "<path d='M21 3 10.5 13.5'/><path d='M21 3l-7 18-3.5-7.5L3 10z'/>",
    "square": "<rect x='4' y='4' width='16' height='16' rx='3'/>",
    "flag": "<path d='M5 21V4h13l-2.5 4.5L18 13H5'/>",
    "filter": "<path d='M3 5h18l-7 8v6l-4 2v-8z'/>",
    "quote": "<path d='M9 7H5.5A2.5 2.5 0 0 0 3 9.5v3A2.5 2.5 0 0 0 5.5 15H7v1.5A2.5 2.5 0 0 1 4.5 19'/><path d='M20 7h-3.5A2.5 2.5 0 0 0 14 9.5v3A2.5 2.5 0 0 0 16.5 15H18v1.5A2.5 2.5 0 0 1 15.5 19'/>",
}


def icon(name, size=16, cls=""):
    cls_attr = f' class="icon {cls}"' if cls else ' class="icon"'
    return (
        f'<svg{cls_attr} width="{size}" height="{size}" viewBox="0 0 24 24" fill="none" '
        "stroke=\"currentColor\" stroke-width=\"1.8\" stroke-linecap=\"round\" "
        f'stroke-linejoin="round" aria-hidden="true">{ICONS[name]}</svg>'
    )


WAVE = [7, 13, 19, 10, 23, 16, 8, 21, 25, 14, 9, 18, 22, 11, 7, 15, 20, 12, 24, 17,
        8, 13, 19, 10, 16, 22, 9, 14, 18, 11, 21, 15, 8, 12, 20, 13, 17, 10, 23, 16]


def wave(played=0, total=len(WAVE)):
    bars = []
    for i, h in enumerate(WAVE):
        cls = "played" if i < played else ""
        bars.append(f'<i class="{cls}" style="height:{h}px"></i>' if cls
                    else f'<i style="height:{h}px"></i>')
    return '<div class="wave">' + "".join(bars) + "</div>"


# ── 侧栏 ─────────────────────────────────────────────────────────────────

def nav_item(key, label, active=False, badge=None, ic="file", pulse=None):
    cls = "nav-item active" if active else "nav-item"
    bd = f'<span class="chip-count">{badge}</span>' if badge is not None else ""
    dot = f'<span class="pulse{(" amber" if pulse == "amber" else "")}"></span>' if pulse else ""
    return f'<a class="{cls}" href="#">{icon(ic, 16)}{dot}{label}{bd}</a>'


def sidebar(active):
    def n(key, label, ic, name=None, badge=None, pulse=None):
        k = name or key
        return nav_item(k, label, active=(k == active), badge=badge, ic=ic, pulse=pulse)

    return f"""
    <aside class="sidebar">
      <a class="brand" href="#">
        <span class="brand-mark">M<span>.</span></span>
        <span class="brand-copy"><strong>会议</strong><small>sdemo.meeting · 本地</small></span>
      </a>

      <div class="nav-group-label">会议</div>
      <div class="nav">
        {n("全部会议", "全部会议", "calendar", badge="24")}
        {n("进行中", "进行中", "mic", pulse="red")}
        {n("待确认", "待确认", "inbox", badge="2")}
        {n("已归档", "已归档", "check")}
      </div>

      <div class="nav-group-label">类型</div>
      <div class="nav">
        {n("周会", "周会", "layers")}
        {n("客户沟通", "客户沟通", "users")}
        {n("1:1", "1:1", "quote")}
        {n("复盘", "复盘", "refresh")}
      </div>

      <div class="nav-group-label">其它</div>
      <div class="nav">
        {n("回收站", "回收站", "trash")}
        {n("设置", "设置", "settings")}
      </div>

      <div class="sidebar-spacer"></div>

      <div class="sidebar-note">
        <div class="sidebar-note-icon">{icon("database", 16)}</div>
        <div>
          <strong>2 场会议待确认</strong>
          <p>确认后结论与转写一并归档到知识库。</p>
        </div>
      </div>

      <div class="sidebar-foot">
        <div class="avatar">L</div>
        <div class="sidebar-foot-copy">
          <strong>本地 · 仅本机</strong>
          <small>v0.1.0 · 3.6 GB</small>
        </div>
        <button class="icon-btn" title="主题">{icon("moon", 16)}</button>
      </div>
    </aside>
    """


def kb_sidebar(active):
    def n(key, label, ic, name=None, badge=None):
        k = name or key
        return nav_item(k, label, active=(k == active), badge=badge, ic=ic)

    return f"""
    <aside class="sidebar">
      <a class="brand" href="#">
        <span class="brand-mark">K<span>.</span></span>
        <span class="brand-copy"><strong>知识库</strong><small>sdemo.kb · 本地</small></span>
      </a>

      <div class="nav-group-label">库</div>
      <div class="nav">
        {n("全部文档", "全部文档", "file")}
        {n("待归档", "待归档", "inbox", badge="9")}
        {n("检索", "检索", "search")}
        {n("导入", "导入", "download")}
      </div>

      <div class="nav-group-label">分类</div>
      <div class="nav">
        {n("需求", "需求", "target")}
        {n("会议纪要", "会议纪要", "calendar")}
        {n("决策", "决策", "check")}
        {n("资料", "资料", "layers")}
      </div>

      <div class="nav-group-label">其它</div>
      <div class="nav">
        {n("回收站", "回收站", "trash")}
        {n("设置", "设置", "settings")}
      </div>

      <div class="sidebar-spacer"></div>

      <div class="sidebar-foot">
        <div class="avatar">L</div>
        <div class="sidebar-foot-copy">
          <strong>本地知识库</strong>
          <small>v0.1.0 · 2.4 GB</small>
        </div>
        <button class="icon-btn" title="主题">{icon("moon", 16)}</button>
      </div>
    </aside>
    """


def shell(title, desc, actions, body, active, eyebrow="sdemo.meeting", theme="dark",
          side=None, foot=""):
    theme_attr = ' data-theme="light"' if theme == "light" else ""
    return f"""<!DOCTYPE html>
<html lang="zh-CN"{theme_attr}>
<head>
<meta charset="utf-8">
<title>sdemo.meeting · {title}</title>
<link rel="stylesheet" href="sdemo-ui.css">
<link rel="stylesheet" href="meeting.css">
</head>
<body>
<div class="app-shell">
  {side or sidebar(active)}
  <div class="main-column">
    <header class="topbar">
      <div class="topbar-title">
        <span class="eyebrow">{eyebrow}</span>
        <h1>{title}</h1>
        <p class="topbar-desc">{desc}</p>
      </div>
      <div class="topbar-actions">{actions}</div>
    </header>
    {body}
    {foot}
  </div>
</div>
</body>
</html>
"""


SEARCH = (
    '<label class="search-box">'
    + icon("search", 15)
    + '<input placeholder="搜索会议、议题或某句话">'
    + "<kbd>Ctrl K</kbd>"
    + "</label>"
)


# ── 01 会议列表 ──────────────────────────────────────────────────────────

def mtg_row(day, wd, title, meta, status, kb):
    return f"""
      <a class="mtg-row" href="02-meeting.html">
        <span class="date-block"><strong>{day}</strong><small>{wd}</small></span>
        <span>
          <span class="mtg-title">{title}</span>
          <span class="mtg-meta">{meta}</span>
        </span>
        <span class="mtg-tail">{kb}{status}{icon("chevron-right", 15, "dim")}</span>
      </a>"""


def screen_01():
    rows_now = "".join([
        mtg_row("13", "周四", "客户 A 需求澄清", "5 位参与人 · 38 分钟 · 2 个议题",
                '<span class="status-pill is-muted">已归档</span>',
                f'<span class="kb-badge ok">{icon("database", 11)}知识库 · v2</span>'),
        mtg_row("12", "周三", "周会 · 部署方案评审", "6 位参与人 · 47 分钟 · 3 个议题",
                '<span class="status-pill is-accent">待确认</span>',
                f'<span class="kb-badge pending">{icon("clock", 11)}已投影 v3</span>'),
        mtg_row("11", "周二", "1:1 · 张伟", "2 位参与人 · 22 分钟",
                '<span class="status-pill is-muted">已归档</span>',
                f'<span class="kb-badge ok">{icon("database", 11)}知识库 · v1</span>'),
    ])
    rows_old = "".join([
        mtg_row("06", "周五", "复盘 · P4 知识库上线", "4 位参与人 · 63 分钟 · 4 个议题",
                '<span class="status-pill is-muted">已归档</span>',
                f'<span class="kb-badge queue">{icon("refresh", 11)}2 次更新排队中</span>'),
        mtg_row("04", "周三", "周会 · 排期对齐", "6 位参与人 · 51 分钟 · 3 个议题",
                '<span class="status-pill is-muted">已归档</span>',
                f'<span class="kb-badge ok">{icon("database", 11)}知识库 · v4</span>'),
        mtg_row("30", "周四", "客户 B 方案评审", "4 位参与人 · 41 分钟 · 2 个议题",
                '<span class="status-pill is-muted">已归档</span>',
                f'<span class="kb-badge ok">{icon("database", 11)}知识库 · v2</span>'),
    ])

    body = f"""
    <main class="content">
      <div class="live-card">
        <span class="pulse"></span>
        <span class="tc" style="font-size:19px;font-weight:300;color:var(--text)">00:12:34</span>
        <span class="live-copy">
          <strong>产品例会 · 正在录音</strong>
          <small>麦克风 · 48 kHz · 预计 30 分钟 · 已写 3.1 MB</small>
        </span>
        {wave(8)}
        <button class="btn btn-ghost btn-sm">{icon("pause", 14)}暂停</button>
        <button class="btn btn-primary btn-sm">{icon("stop", 13)}结束并转写</button>
      </div>

      <div class="group-label">本周</div>
      <div class="mtg-list">{rows_now}</div>

      <div class="group-label">更早</div>
      <div class="mtg-list">{rows_old}</div>
    </main>
    """

    actions = (SEARCH
               + f'<button class="btn btn-ghost btn-sm">{icon("download", 15)}导入录音</button>'
               + f'<button class="btn btn-primary">{icon("mic", 15)}开始录音</button>')

    return shell(
        "会议",
        "录音、转写、议题与决议都在本地；确认后结论与转写一并归档到知识库。",
        actions, body, "全部会议",
    )


# ── 02 会议详情 ──────────────────────────────────────────────────────────

SPEAKERS = [("李四", "sp-2"), ("我", "sp-1"), ("张三", "sp-3"), ("王五", "sp-4")]


def seg(tc, who, cls, text, hl=False, tag=None):
    tools = (f'<div class="seg-tools"><a href="#">▶ 播放这一段</a>'
             f'<a href="#">{icon("flag", 11)}标为决议</a>'
             f'<a href="#">{icon("users", 11)}标注说话人</a>'
             f'<a href="#">{icon("quote", 11)}在 Agent 中追问</a></div>')
    tag_html = f'<span class="seg-tag">{icon("check", 10)}{tag}</span>' if tag else ""
    return f"""
      <div class="seg{' seg-hl' if hl else ''}">
        <span class="tc">{tc}</span>
        <div class="seg-body">
          <span class="speaker {cls}"><i></i>{who}</span>{tag_html}
          <p>{text}</p>
          {tools}
        </div>
      </div>"""


def room_main():
    return f"""
      <div class="player">
        <button class="play-btn">{icon("play", 15)}</button>
        <span class="tc">12:34</span>
        {wave(9)}
        <span class="tc">47:18</span>
        <span class="status-pill">1.0×</span>
      </div>
      <div class="seg-list">
        {seg("00:12:31", "李四", "sp-2",
             "这个部署方案我还是有顾虑。现在是一把梭全量切，回滚要多长时间，好像没人算过。")}
        {seg("00:12:48", "我", "sp-1",
             "回滚脚本上周跑过一次，大概 8 分钟，但只在小流量上验证过。")}
        {seg("00:13:02", "李四", "sp-2",
             "8 分钟是<mark>全量还是灰度</mark>？我问的是出问题之后多久能真的回到旧版本。",
             hl=True, tag="决议 2 的来源")}
        {seg("00:13:20", "张三", "sp-3",
             "建议先灰度两周，20% 流量跑稳了再切全量。")}
        {seg("00:13:35", "我", "sp-1",
             "那就按灰度两周定，回滚时间写进验收标准。")}
        {seg("00:13:41", "王五", "sp-4",
             "回滚演练的记录我来补，下周三之前给。")}
        {seg("00:14:02", "李四", "sp-2",
             "可以，但我保留意见：灰度期间的指标如果看不出来，两周等于白跑。")}
      </div>
    """


def room_rail():
    items = [("00:00", "开场 · 5 位参与人", False),
             ("06:12", "议题 1 · 客户 A 需求变更", True),
             ("21:40", "议题 2 · 内部工具重构", False),
             ("33:05", "议题 3 · 下周排期", False),
             ("47:18", "结束", False)]
    rows = "".join(
        f'<a class="rail-item{" active" if act else ""}" href="#">'
        f'<span class="tc">{t}</span><span>{label}</span></a>' for t, label, act in items)
    spk = "".join(
        f'<div class="spk-row"><span class="speaker {cls}"><i></i>{who}</span>'
        f'<span class="count">{n} 段</span></div>' for who, cls, n in
        [("李四", "sp-2", 41), ("我", "sp-1", 38), ("张三", "sp-3", 33), ("王五", "sp-4", 22)])
    return f"""
      <div class="rail-label">时间轴</div>
      <div class="rail">{rows}</div>
      <div class="rail-label" style="margin-top:20px">说话人 · v1 手工标注</div>
      <div class="spk-list">{spk}</div>
      <div class="rail-label" style="margin-top:20px">来源</div>
      <div class="spk-list">
        <div class="spk-row">{icon("mic", 12)}<b>环回 + 麦克风</b></div>
        <div class="spk-row">{icon("database", 12)}whisper large-v3-turbo</div>
      </div>
    """


def room_side():
    return f"""
      <div class="card side-card">
        <div class="card-head">
          <div>
            <span class="eyebrow">Knowledge Base</span>
            <strong>已投影到知识库</strong>
          </div>
          <span class="status-pill is-success">已归档</span>
        </div>
        <div class="proj-stats">
          <div class="proj-stat">{icon("check", 12)}结论<b>4</b> 块<span class="push-right">durable</span></div>
          <div class="proj-stat">{icon("quote", 12)}证据<b>134</b> 段<span class="push-right">durable</span></div>
          <div class="proj-stat">{icon("clock", 12)}版本<b>v3</b><span class="push-right">2 分钟前</span></div>
        </div>
        <div class="hstack">
          <button class="btn btn-ghost btn-sm" style="flex:1">{icon("external", 13)}在知识库中打开</button>
          <button class="btn btn-ghost btn-sm" style="flex:1">{icon("refresh", 13)}变更</button>
        </div>
      </div>

      <div class="card side-card">
        <div class="card-head">
          <div><strong>议题 · 3</strong></div>
          <button class="icon-btn">{icon("plus", 14)}</button>
        </div>
        <div class="topic-item">
          <span class="topic-no">01</span>
          <div>
            <div class="topic-title">客户 A 需求变更</div>
            <div class="topic-meta">
              <span class="ref-chip">{icon("link", 9)}kb/doc/01H9…@v7</span>
              <span class="status-pill">决议 1</span>
              <span class="status-pill">候选 2</span>
            </div>
          </div>
        </div>
        <div class="topic-item">
          <span class="topic-no">02</span>
          <div>
            <div class="topic-title">内部工具重构进度</div>
            <div class="topic-meta">
              <span class="ref-chip">{icon("link", 9)}agent/workspace/sdemo</span>
              <span class="status-pill">决议 1</span>
              <span class="status-pill">候选 3</span>
            </div>
          </div>
        </div>
        <div class="topic-item">
          <span class="topic-no">03</span>
          <div>
            <div class="topic-title">下周排期</div>
            <div class="topic-meta"><span class="status-pill">候选 1</span></div>
          </div>
        </div>
      </div>

      <div class="card side-card">
        <div class="card-head">
          <div><strong>待办候选 · 3</strong></div>
          <span class="status-pill is-muted">不直接建待办</span>
        </div>
        <div class="act-row">
          <span class="act-box">{icon("square", 13)}</span>
          <div style="flex:1">
            <div class="act-title">补回滚演练记录</div>
            <div class="act-meta">王五 · 下周三前 · <span style="color:var(--accent)">待安排</span></div>
          </div>
        </div>
        <div class="act-row">
          <span class="act-box">{icon("square", 13)}</span>
          <div style="flex:1">
            <div class="act-title">灰度方案写进验收标准</div>
            <div class="act-meta">我 · 本周 · <span style="color:var(--accent)">待安排</span></div>
          </div>
        </div>
        <div class="act-row">
          <span class="act-box">{icon("check", 13)}</span>
          <div style="flex:1">
            <div class="act-title">通知客户 A 变更影响</div>
            <div class="act-meta">李四 · 已安排 · <span class="tc">planner/todo/01HB…</span></div>
          </div>
        </div>
        <button class="btn btn-ghost btn-sm" style="width:100%;margin-top:11px">
          {icon("send", 13)}交给 Agent 安排
        </button>
      </div>
    """


def room_body():
    return f"""
    <main class="content mtg-flush">
      <div class="mtg-room">
        <div class="col-rail">{room_rail()}</div>
        <div class="col-main">{room_main()}</div>
        <div class="col-side">{room_side()}</div>
      </div>
    </main>
    """


SYNC_FOOT = """
    <div class="sync-bar">
      <span class="status-pill is-success">已同步</span>
      <span><b>v3</b> · 138 块 · 结论 4 + 证据 134</span>
      <span class="dim">·</span>
      <span>3 分钟前完成投影</span>
      <span class="push-right"></span>
      <span class="uri-chip">sdemo.kb/doc/01HA7…@v3</span>
      <a class="text-link" href="05-kb.html">查看投影结果</a>
    </div>
    """


def screen_02(theme="dark"):
    actions = (f'<button class="btn btn-ghost btn-sm">{icon("send", 15)}在 Agent 中打开</button>'
               f'<button class="btn btn-ghost btn-sm">{icon("download", 15)}导出</button>'
               f'<button class="btn btn-primary">{icon("database", 15)}确认并归档</button>')
    return shell(
        "周会 · 部署方案评审",
        "2026-11-12 周三 09:30 · 47 分钟 · 6 位参与人 · 3 个议题",
        actions, room_body(), "待确认", theme=theme, foot=SYNC_FOOT,
    )


# ── 03 确认并归档 ────────────────────────────────────────────────────────

def screen_03():
    modal = """
    <div class="modal-backdrop">
      <div class="modal modal-md" style="width:min(640px,100%)">
        <div class="modal-head">
          <div>
            <span class="eyebrow">Archive to Knowledge Base</span>
            <h2 style="margin:0;font-size:17px;font-weight:600">确认并归档这场会议</h2>
            <p class="topbar-desc" style="margin-top:4px">
              结论与转写一起进入知识库；之后在本应用删除会议，知识库那份仍然在。
            </p>
          </div>
          <button class="icon-btn">""" + icon("x", 16) + """</button>
        </div>

        <div class="modal-body" style="padding:2px 21px 18px">
          <div class="pack-list" style="margin-bottom:14px">
            <div class="pack-row">""" + icon("check", 14) + """
              结论（议题小结、决议）<b>4 块</b>
              <span class="push-right">kind=decision</span>
            </div>
            <div class="pack-row">""" + icon("quote", 14) + """
              转写<b>134 段</b>，带时间码，可从知识库跳回录音位置
              <span class="push-right">3 个议题 · 47 分钟</span>
            </div>
            <div class="pack-row">""" + icon("mic", 14) + """
              录音原件 <b>18.4 MB</b>（M4A）
              <span class="push-right">可单独保留/清理</span>
            </div>
            <div class="pack-row">""" + icon("file", 14) + """
              分析履历 <b>2 条</b>（whisper large-v3-turbo / 总结 v3）
              <span class="push-right">仅留在本应用</span>
            </div>
          </div>

          <div class="opt-list" style="margin-bottom:14px">
            <div class="opt-card on">
              <span class="radio"></span>
              <div>
                <strong>归档 · 永久保留</strong>
                <small>知识和证据都不再有 TTL；可被 Agent 检索、可被其它文档引用。</small>
              </div>
            </div>
            <div class="opt-card">
              <span class="radio"></span>
              <div>
                <strong>仅暂存 · 30 天后自动清理</strong>
                <small>先进知识库，等你在知识库里再决定要不要留。</small>
              </div>
            </div>
          </div>

          <div class="callout info">
            """ + icon("info", 15) + """
            <div>
              归档之后，<b>知识库里的副本与这场会议解耦</b>：在本应用删除会议只会删掉本地录音与工作稿，
              知识库的结论和转写保留下来，并保留到录音的原链接。要清掉知识库那份，得去知识库删除。
            </div>
          </div>
        </div>

        <div class="modal-foot">
          <span class="muted" style="font-size:10.5px;margin-right:auto">
            将写入 knowledge/%APPDATA%/sdemo.kb · 预计 1.9 MB 文本
          </span>
          <button class="btn btn-ghost">取消</button>
          <button class="btn btn-primary">""" + icon("database", 15) + """确认并归档</button>
        </div>
      </div>
    </div>
    """

    return shell(
        "周会 · 部署方案评审",
        "2026-11-12 周三 09:30 · 47 分钟 · 6 位参与人 · 3 个议题",
        f'<button class="btn btn-ghost btn-sm">{icon("download", 15)}导出</button>',
        room_body(), "待确认", foot=SYNC_FOOT,
    )[: -len("</body>\n</html>\n")] + modal + "</body>\n</html>\n"


# ── 04 候选 → Agent ──────────────────────────────────────────────────────

def cand(title, meta, on=True, status="待安排"):
    return f"""
      <div class="cand-row{' on' if on else ''}">
        <span class="check-box">{icon("check", 11)}</span>
        <div>
          <div class="mtg-title" style="font-size:12.5px">{title}</div>
          <div class="mtg-meta">{meta}</div>
        </div>
        <span class="status-pill {'is-accent' if status == '待安排' else 'is-muted'}">{status}</span>
      </div>"""


def screen_04():
    body = f"""
    <main class="content">
      <div class="act-split">
        <div>
          <div class="hstack" style="margin-bottom:14px">
            <span class="section-title" style="margin:0">来自「周会 · 部署方案评审」的候选</span>
            <span class="status-pill">3 条 · 已选 2 条</span>
            <span class="push-right"></span>
            <button class="btn btn-ghost btn-sm">{icon("filter", 14)}按议题</button>
          </div>
          <div class="vstack">
            {cand("补回滚演练记录", "王五 · 下周三前 · 议题 1 客户 A 需求变更 · 引自 00:13:41")}
            {cand("灰度方案写进验收标准", "我 · 本周 · 议题 1 客户 A 需求变更 · 引自 00:13:35")}
            {cand("周会排期改到周四", "我 · 议题 3 下周排期 · 引自 00:33:12", on=False)}
          </div>

          <div class="callout warn" style="margin-top:16px">
            {icon("warning", 15)}
            <div>候选不等于待办。会议里「说了但没定」的事不会因为出现在这里就变成承诺，
            所以这一步只是把它们交给 Agent，由它带着 Planner 的上下文判断重复与优先级。</div>
          </div>

          <div class="flow">
            <div class="flow-step now"><b>1 · 候选</b>会议只写 mtg_action，status=candidate</div>
            <div class="flow-step"><b>2 · Agent 编排</b>判重、合并、按清单优先级排</div>
            <div class="flow-step"><b>3 · Planner 审阅</b>草稿在你手里逐条 ✓ / ✎ / ✕</div>
            <div class="flow-step"><b>4 · 回写</b>Agent 记 plannerRef，候选转「已安排」</div>
          </div>
        </div>

        <div class="agent-card">
          <div class="agent-head">
            <span class="agent-mark">A</span>
            <div style="flex:1">
              <strong style="font-size:12.5px">DesktopAgent 请求执行</strong>
              <div class="muted" style="font-size:10.5px">2 个动作 · 中风险 · 需要你确认一次</div>
            </div>
            <span class="status-pill is-accent">待审批</span>
          </div>
          <div class="agent-body">
            <div class="agent-step"><span class="idx">1</span>
              <div><b>planner.todo_create_draft</b> × 2<br>
              <span class="muted">按现有清单判重、合并，生成草稿</span></div>
            </div>
            <div class="agent-step"><span class="idx">2</span>
              <div><b>meeting.patch</b> × 2<br>
              <span class="muted">回写 <span class="tc">planner/todo/…</span>，候选转「已安排」</span></div>
            </div>
            <hr class="sep">
            <div class="callout" style="font-size:11px">
              {icon("info", 14)}
              <div>草稿不会直接入库。你会在 Planner 的界面里逐条审阅，那里才知道清单里已经有什么。</div>
            </div>
            <div class="hstack" style="margin-top:12px">
              <button class="btn btn-primary btn-sm" style="flex:1">{icon("check", 14)}允许并审阅草稿</button>
              <button class="btn btn-ghost btn-sm">拒绝</button>
            </div>
            <div class="muted" style="margin-top:9px;font-size:10px;text-align:center">
              记住本会话 · 之后同类动作不再询问
            </div>
          </div>
        </div>
      </div>
    </main>
    """

    actions = (f'<button class="btn btn-ghost btn-sm">{icon("users", 15)}按负责人</button>'
               f'<button class="btn btn-primary">{icon("send", 15)}交给 Agent 安排</button>')

    return shell(
        "待办候选",
        "会议只产出候选；安排给谁、什么时候做，由 Agent 带着 Planner 的上下文来定。",
        actions, body, "待确认",
    )


# ── 05 知识库里的会议文档 ────────────────────────────────────────────────

def screen_05():
    body = f"""
    <main class="content">
      <div class="proj-split">
        <div>
          <div class="doc-head">
            <span class="eyebrow">Durable · 最新 v3 · 2026-11-12 10:31</span>
            <h2 class="doc-title">周会 · 部署方案评审</h2>
            <div class="doc-meta">
              <span class="ret ret-durable"><span class="ret-dot"></span>归档</span>
              <span class="ret ret-link"><span class="ret-dot"></span>2 处引用</span>
              <span class="uri-chip">sdemo.kb/doc/01HA7…@v3</span>
              <span>来源：sdemo.meeting · 已与源会议解耦</span>
            </div>
          </div>

          <div class="blk">
            <h3>议题 1 · 客户 A 需求变更</h3>
            <p>结论：先按灰度两周推进，20% 流量稳定后再切全量；回滚时间（当前 8 分钟）
            写进验收标准，由王五补齐回滚演练记录。</p>
            <span class="blk-anchor playable">{icon("play", 10)}00:06:12 · 跳回录音</span>
          </div>

          <div class="blk">
            <h3>决议 2 · 灰度两周后切全量</h3>
            <p>李四对「全量切换」提出异议，要求先明确回滚耗时口径（全量还是灰度）。
            会议最终按灰度两周定为决议，李四保留意见。</p>
            <span class="blk-anchor playable">{icon("play", 10)}00:13:02 · 跳回录音</span>
          </div>

          <div class="blk">
            <h3>原始转写 · 134 段</h3>
            <div class="evidence">
              <div class="evidence-head">
                {icon("quote", 13)}按时间码索引 · 只在知识库里也能读全文
                <span class="push-right"></span>
                <span class="tc">00:12:31 – 00:14:02</span>
              </div>
              <div class="evidence-body">
                <div class="ev-line"><span class="tc">00:12:31</span><span>李四：这个部署方案我还是有顾虑。现在是一把梭全量切，回滚要多长时间，好像没人算过。</span></div>
                <div class="ev-line"><span class="tc">00:13:02</span><span>李四：8 分钟是全量还是灰度？我问的是出问题之后多久能真的回到旧版本。</span></div>
                <div class="ev-line"><span class="tc">00:13:20</span><span>张三：建议先灰度两周，20% 流量跑稳了再切全量。</span></div>
                <div class="ev-line"><span class="tc">00:14:02</span><span>李四：可以，但我保留意见……</span></div>
                <div class="ev-line"><span class="tc">…</span><span class="dim">展开其余 130 段</span></div>
              </div>
            </div>
          </div>
        </div>

        <div>
          <div class="card side-card">
            <div class="card-head"><div><strong>版本链</strong></div><span class="status-pill">3 版</span></div>
            <div class="kv-list" style="font-size:11.5px">
              <div class="kv"><span class="muted">v3 · 当前</span><span>确认后归档</span></div>
              <div class="kv"><span class="muted">v2</span><span>重跑分析（模型换代）</span></div>
              <div class="kv"><span class="muted">v1</span><span>会议结束自动投影</span></div>
            </div>
          </div>

          <div class="card side-card">
            <div class="card-head"><div><strong>来源</strong></div></div>
            <div class="kv-list" style="font-size:11.5px">
              <div class="kv"><span class="muted">应用</span><span>sdemo.meeting</span></div>
              <div class="kv"><span class="muted">实体</span><span class="tc">meeting/01HA2…</span></div>
              <div class="kv"><span class="muted">录音</span><span>18.4 MB · 已归档</span></div>
            </div>
            <div class="callout" style="margin-top:11px;font-size:10.5px">
              {icon("info", 13)}
              <div>源会议已在 sdemo.meeting 中删除，这篇文档独立保留。</div>
            </div>
          </div>

          <div class="card side-card">
            <div class="card-head"><div><strong>谁在用</strong></div></div>
            <div class="mv-row" style="font-size:11.5px;color:var(--muted)">
              · planner/todo/01HB…（议题 1 的验收标准）<br>
              · kb/doc/01H9…@v7（部署方案需求）
            </div>
          </div>
        </div>
      </div>
    </main>
    """

    return shell(
        "周会 · 部署方案评审",
        "这是一场会议投影到知识库之后的样子：结论在上，原始转写在下，每块都能跳回录音。",
        f'<button class="btn btn-ghost btn-sm">{icon("external", 15)}在会议中打开</button>'
        f'<button class="btn btn-ghost btn-sm">{icon("refresh", 15)}与 v2 比较</button>',
        body, "会议纪要", eyebrow="Knowledge Base", side=kb_sidebar("会议纪要"),
    )


# ── 06 桌面录音组件 ──────────────────────────────────────────────────────

def screen_06():
    return f"""<!DOCTYPE html>
<html lang="zh-CN">
<head>
<meta charset="utf-8">
<title>sdemo.meeting · 录音组件</title>
<link rel="stylesheet" href="sdemo-ui.css">
<link rel="stylesheet" href="meeting.css">
</head>
<body>
<div class="desktop">
  <div>
    <div class="widget">
      <div class="widget-top">
        <span class="pulse"></span>录音中 · 麦克风
        <span style="margin-left:auto">48 kHz</span>
      </div>
      <div class="widget-time">00:12:34</div>
      <div class="widget-title">产品例会 · 2026-11-15</div>
      <div class="widget-actions">
        <button class="widget-btn">{icon("pause", 13)}暂停</button>
        <button class="widget-btn stop">{icon("stop", 12)}结束</button>
      </div>
      <div class="widget-foot">结束会自动转写，之后在会议列表里出纪要</div>
    </div>
    <div class="caption">
      半透明、置顶、不抢焦点；点一下回到主窗口。<br>
      与待办小组件同一套 <span style="font-family:monospace">native</span> 组件机制。
    </div>
  </div>
</div>
</body>
</html>
"""


SCREENS = {
    "01-meetings": screen_01,
    "02-meeting": screen_02,
    "03-finalize": screen_03,
    "04-actions": screen_04,
    "05-kb": screen_05,
    "06-widget": screen_06,
}

if __name__ == "__main__":
    for name, fn in SCREENS.items():
        out = HERE / f"{name}.html"
        out.write_text(fn(), encoding="utf-8")
        print(f"wrote {out.name}")
    # 浅色变体
    light = screen_02(theme="light")
    (HERE / "02-meeting-light.html").write_text(light, encoding="utf-8")
    print("wrote 02-meeting-light.html")

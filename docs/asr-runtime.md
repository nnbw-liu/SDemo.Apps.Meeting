# sdemo.meeting · ASR 运行时

> 状态：**设计稿 v0.3（2026-11-15）· 待真机基准后定稿**
> 修订：v0.2 推翻 v0.1 的主次（whisper 是主力，Qwen3-ASR 是升级档）；v0.3 修正升级档的接入形态（**多模态 chat，不是 `/v1/audio/transcriptions`**），见 §4.2–4.5。
> 范围：本地转写引擎、引擎升级、模型分发、分段与时间戳、性能档位、失败降级
> 关联：[asr-relay 契约](../../../docs/contracts/asr-relay.md)（云端中继，本版不启用）· [meeting-design.md](meeting-design.md) §5 · [requirements.md](requirements.md) MT-03 / MT-04 / MT-05
> 一句话：**whisper 保证"每场都能转完"，Qwen3 负责"转不准的那几段"。**

---

## 0. 一页纸结论

| 问题 | 结论 |
| --- | --- |
| **默认引擎（主力）** | **whisper.cpp `large-v3-turbo` `q8_0`**（874 MB，MIT，99 语种，段级时间戳与标点免费） |
| 低配档 | whisper `small` `q5_1`（190 MB），老机器/快速草稿 |
| **升级档（后备）** | **Qwen3-ASR-1.7B**（Apache-2.0，30 语种 + 22 中文方言，官方 GGUF Q8_0 = 2.5 GB） |
| 升级档接入形态 | **多模态 chat（`/v1/chat/completions` + audio），不是 `/v1/audio/transcriptions`**；Ollama / llama.cpp 都是这个形状（§4.2–4.5） |
| 时间戳增强 | Qwen3-ForcedAligner-0.6B（可选包，字级对齐；不下就退到段级——而段级由我们自己的 VAD 保证） |
| 要不要 Python | **不要**。whisper 走 P/Invoke；Qwen3 走 llama.cpp server / Ollama / 进程内 llama.cpp，均无 Python |
| 云端 | 本版不接。中继契约先写形状，见 [asr-relay.md](../../../docs/contracts/asr-relay.md) |
| 最大未知 | 不是"哪个模型好"，而是**升级触发的准确率**：能不能只把该升级的段挑出来（§3） |

---

## 1. 边界

**做**：本地转写、引擎升级、时间戳、语言判定、术语表、模型分发与校验、进度与取消、失败降级。

**不做**（本版）：

| 不做 | 理由 |
| --- | --- |
| 实时字幕 / 边说边转 | 流式 ASR + 低延迟是另一套工程，见 requirements 非目标 |
| 自动说话人分离 | 走 `IDiarizationProvider` 空实现，v1 手工标注 |
| 云端 ASR 默认开启 | 音频是最敏感的数据；云端只能是显式例外（meeting-design §5 例外条款） |
| **两个引擎都跑一遍再挑结果** | 一场会跑两遍 = 双倍耗时与电量，用户感知糟糕。只在"该升级的段"上升级（§3） |
| 音频上传/同步 | 音频只在本机 `%APPDATA%`，不参与任何同步 |

---

## 2. 两个引擎的分工

### 2.1 为什么 whisper 是主力

不是情怀，是三条硬理由：

| # | 理由 |
| --- | --- |
| 1 | **集成成本最低，且是唯一"确定能跑"的**。一个 `whisper.dll` + 一个 `.bin`，P/Invoke 半天接通，标点和段级时间戳白送。Qwen3 在 .NET 里怎么跑（ONNX 还是侧车）**现在还没有答案**，用它当主力等于把整个产品押在一个未验证的集成上 |
| 2 | **它是 OpenAI 维护、十年生态、MIT**。出问题能自己 debug，上游不会消失 |
| 3 | **中英日三语它本来就够用**：中文好、英文很好、日文好。混说是它的短板，但**绝大多数会议不是全程混说**——一场 47 分钟的会里真正需要混说的可能只有 3–5 分钟 |

### 2.2 Qwen3 补的是 whisper 的结构性缺口

whisper 的缺陷是架构级的：**30 秒窗口 + 单一 `language` token**。中英日混说时它必须整段选一种语言，另一种要么被音译化、要么整段漏掉。这不是调参能修的。

Qwen3-ASR 单模型带语种识别、并且支持极长音频（官方推理框架里是一次 20 分钟；llama.cpp 上按 8 秒窗口预处理，形态不同，见 §4.3），正好绕开这个限制。其余能力（22 种中文方言、其他语种）对我们当前的三语目标是**冗余**，但不需要额外代价就能带上。

### 2.3 所以正确的形状是"阶梯"，不是"二选一"

```
每一场会议
  └─① whisper large-v3-turbo 全量转写（默认，永远先跑）
        └─② 检测：这段是不是转砸了？（§3.1 五个信号）
              └─③ 只把那几段交给 Qwen3 重解
                    └─④ 写回同一场，标注来源引擎
```

**这和 DesktopAgent 的模型路由是同一个哲学**：便宜的先跑，难的升级。差别在于这里升级的粒度是"段"，不是"整个请求"。

---

## 3. 升级触发（本文档的核心）

升级做得好不好，决定了"多一个 1.7B 引擎"是资产还是负债。判错的代价是双向的：漏升级 = 转写质量没救回来；乱升级 = 一场会白跑两遍。

### 3.1 五个信号

| # | 信号 | 判据（初值，待基准校准） | 说明 |
| --- | --- | --- | --- |
| S1 | **语言切换密集** | 10 分钟内 `LANG_SWITCH` 警告 ≥ 5 次 | whisper 的 `language` 会跳，跳一次毁一段 |
| S2 | **低置信段占比高** | 单段 `avg_logprob < -0.8` 或 `no_speech_prob` 异常 | whisper 的 token 概率可直接取 |
| S3 | **幻觉特征** | 静音段出现整句 / 重复短语 / 与上下文不连贯 | whisper 的经典病：无声处"编"出字幕 |
| S4 | **字符集不符** | 声明 `zh` 但输出大量假名/西里尔，或反之 | 说明语言判定错了 |
| S5 | **用户手动标记** | 用户点某段"这段不对" | 最高优先级，永远尊重 |

**S1 与 S4 是 Qwen3 唯一能真正补上的**（多语言切换）；S2 与 S3 主要是"提醒用户看，不是一定要重跑"。

### 3.2 建议式，不擅自执行

检测到信号后**不自动升级**，而是在转写结果上给一条建议：

```
⚠ 第 3 段（00:12:31–00:14:02）检测到中英切换 7 次，可能有漏字
   [用增强引擎重解这一段]   [忽略]
```

理由和"候选不是待办"一致：**机器只提示，重活由用户点头**。1 小时的会重跑一遍可能要 30 分钟，不能在我们觉得"可能不准"的时候擅自占满 CPU。

例外：如果整场信号密度超阈值（如 > 40% 段落命中），直接建议"整场用增强引擎重跑"，减少点击次数。

### 3.3 粒度：逐段重解 > 整场重跑

| 方式 | 耗时 | 数据后果 | 采用 |
| --- | --- | --- | --- |
| 整场重跑 | 一次全量 | 新建 run，切换 `current_run_id` | 仅在信号覆盖 >40% 时建议 |
| **逐段重解** | 只跑命中的段 | 写回同一 run，段上标 `engine` | **默认** |

逐段重解要求 `mtg_segment.engine` 可空，空表示"跟随 run"（§6.3）。这样"一条 run 里混两种引擎"是可表达的，不需要为混合结果造新表。

---

## 4. 运行时与集成

### 4.1 whisper：主线，低风险

| 项 | 决定 |
| --- | --- |
| 形态 | P/Invoke `whisper.dll`（ggml-org/whisper.cpp），进程内 |
| 模型 | `ggml-large-v3-turbo-q8_0.bin`（874 MB）/ `ggml-small-q5_1.bin`（190 MB） |
| 线程 | 默认 `min(8, 物理核数-2)`，可配；转写期间降 UI 线程优先级 |
| GPU | 可选（CUDA / Vulkan / Metal）；不支持就纯 CPU，**CPU 是基线** |
| 语言 | 传 `language` 提示（zh/en/ja），**不靠自动检测**（见 §5.3） |
| 时间戳 | 段级（token timestamps 可做到接近句级），满足 `timecode` anchor |
| 取消 | `whisper_full` 的 `abort_callback` 返回 true，2 秒内生效 |

### 4.2 先把"标准 OpenAI 格式"拆开：ASR 上有两种，互不兼容

| 形状 | 端点 | 请求 | 返回 | 谁提供 |
| --- | --- | --- | --- | --- |
| **A. 转写端点** | `POST /v1/audio/transcriptions` | multipart 文件 + `model` | `{text}`；`verbose_json` 时带 segments 时间戳 | OpenAI、Groq、whisper-server、OpenASR |
| **B. 多模态对话** | `POST /v1/chat/completions` | messages + `input_audio`（base64） | 普通 chat completion，文本在 `content` 里 | vLLM、llama.cpp server、Ollama、Qwen3-ASR 系 |

**Qwen3-ASR 是 B，不是 A。** 这个区别直接决定三件事：有没有时间戳、能不能一次喂很长的音频、输出要不要解析。

### 4.3 实测现状（2026-11 查证）

**Ollama**

- stock Ollama **没有** `/v1/audio/transcriptions`。社区包 `frozenlab/qwen3-asr` 用官方 GGUF（`1.7b` Q8_0 = 2.5 GB / `0.6b` = 1.0 GB），走的是 `/api/chat`，**把音频 base64 塞进 `images` 字段、prompt 留空**；返回形如 `language Chinese<asr_text>…`，要剥前缀。
- 单次约 **≤ 60 秒**音频（16 kHz 单声道 WAV）。
- **结论：它是一个"多模态 chat 模型"，不是一个"ASR 端点"。** 说"通过标准 OpenAI 格式接入"在 Ollama 上并不成立——除非我们再加一层适配。

**llama.cpp**

- 官方已支持 `ggml-org/Qwen3-ASR-1.7B-GGUF`（[PR #19441](https://github.com/ggml-org/llama.cpp/pull/19441) 之后进主线）。
- ⚠️ 但音频编码器要求 **windowed attention（窗口 1–8 秒）**，llama.cpp 在预处理层把音频切成 8 秒块；一次喂 30 秒全注意力会出乱码。**"一次吃 20 分钟"是 Qwen 官方推理框架的能力，不是 llama.cpp 的能力。**
- 形态同样是 chat completions（mtmd），不是 transcriptions。

### 4.4 结论：升级档走 `ChatAudioAsrProvider`

| 后果 | 应对 |
| --- | --- |
| **没有时间戳** | 我们自己 VAD 切 **20–60 秒**段，**段边界即时间戳**。秒级精度够 `timecode` anchor，而且不依赖模型能力——反而更可控 |
| 单次 ≤ 60 秒（Ollama） | 与分段策略天然对齐；llama.cpp 内部的 8 秒窗口由它自己处理，我们不重复切 |
| **输出要解析** | "宽容但严格"的解析器：剥 `language xx` 前缀 → 取 `<asr_text>` 之后 → 去 markdown/空白；失败重试一次，再失败标 `LOW_CONF` |
| 本地免费、云端按 token | 成本模型与 whisper 不同，本地场景可忽略 |

托管方式三选一，**推荐 ②**：

| # | 方式 | 优 | 劣 |
| --- | --- | --- | --- |
| ① | 用户自己跑 Ollama，我们只探测 | 零打包成本 | 用户要自己装、自己拉 2.5 GB；版本不受控 |
| ② | **随包分发 llama.cpp server + GGUF** | 版本可控、直接吃官方 GGUF、OpenAI 兼容、无 Python | 多一个进程要管生命周期，二进制增量约 20–30 MB |
| ③ | 进程内 P/Invoke llama.cpp（mtmd） | 无额外进程 | mtmd API 比 whisper.cpp 复杂得多，v1 不划算 |

**ONNX 方案降为备选。** 官方既然已出 GGUF，再自己啃 ONNX 导出 + KV cache 就是重复劳动。

### 4.5 谁调用它：Meeting 直连，但要自己收紧

**答：Meeting 进程直接连 `http://127.0.0.1:<port>`。** 这不违反"应用零模型凭证"，因为界线从来不是"本地还是云端"，而是两条：

| 判据 | 云端 ASR | 本地 ASR 端点 | 本地 chat 端点 |
| --- | --- | --- | --- |
| 有凭证吗 | ✅ 有 | ❌ 无 | ❌ 无 |
| 有策略吗（选模型/拼上下文/路由） | — | ❌ 固定一次 audio→text | ✅ 有 |
| 归谁 | Agent（中继） | **Meeting 的 `AsrProvider`** | Agent |

为了让这条线真的立得住，`ChatAudioAsrProvider` 必须自己收紧：

1. endpoint **只接受 loopback**（`127.0.0.1` / `localhost`），填外网地址直接拒绝；
2. 只发**固定的系统提示 + 音频片段**，不接受任意 messages 透传；
3. 只解析成 `AsrSegment`，**不把原始 completion 暴露给上层**。

三条加起来，它就是一个"长得像 chat 的 ASR 库"，而不是"应用偷偷拿到了模型能力"。

**注意：这个决策已经被降级了。** v0.1 里它是"决定默认引擎"，现在它只决定"升级档能不能用"：

- 不急，放在阶段 C 做；
- spike 失败也不影响发布——whisper 单引擎已是完整产品；
- 失败就砍掉升级档，S1/S4 降级为"提示用户这段可能不准"，而不是"重解"。

---

## 5. 分段、时间戳与语言

### 5.1 流水线（whisper 主路径）

```
音频（16k 单声道，双路分别处理）
  → VAD（静音切分）
  → whisper large-v3-turbo（30s 窗口滑，带 token 时间戳）
  → 合并：跨窗去重、按时间排序、说话人（v1 手工）附着
  → 质检：算 S1–S4 信号，产出 warnings
  → mtg_segment（run_id + start_ms/end_ms/text/confidence/speaker/lang）
```

**升级路径（只跑命中的段）**：

```
命中段（S1–S5）
  → 按 VAD 边界切 20–60 秒片段（≤ Ollama 的单次上限）
  → POST /v1/chat/completions（audio + 固定提示）
  → 解析：剥 language 前缀 → 取 <asr_text>
  → 写回同一 run 的 mtg_segment，engine = "qwen3-asr"，start/end 用我们的切段边界
```

**段边界即时间戳**：升级档没有原生时间戳，但因为是我们自己切的段，时间精度天然等于切段精度（20–60 秒）。要更细才需要 ForcedAligner。

### 5.2 时间戳精度

| 引擎 | 精度 | 对 KB anchor 的影响 |
| --- | --- | --- |
| whisper 段级 | 约 1–4 秒 | ✅ 够用（"跳到那句话"） |
| whisper + token timestamps | 约 0.3–1 秒 | ✅ 更好 |
| Qwen3 + ForcedAligner | 约 40 ms | ✅ 最佳，但多 1.2 GB 可选包 |

**结论：anchor 一律是 `{startMs, endMs}`，精度由引擎能力声明决定，上层不感知。** ForcedAligner 不是必需项。

### 5.3 语言：永远给提示，不让模型自己猜

v1 固定传 `language`（会议创建时选 中/英/日/自动）。理由：

- 自动检测在混说时会来回跳，**跳一次毁一段**；
- 固定语言能显著减少 whisper 的"翻译腔"和幻觉（它会把外语段直接翻成英文）；
- `auto` 只在用户明确选择时启用，且转写完成后把检测到的语言写进 segment，供后续分析用。

---

## 6. 统一数据契约

### 6.1 引擎接口

```csharp
public interface IAsrProvider
{
    string Id { get; }                          // "whisper-cpp" / "qwen3-asr"
    AsrCapabilities Capabilities { get; }
    IReadOnlyList<AsrModelPack> Packs { get; }  // 已下 / 可下
    Task<AsrResult> TranscribeAsync(AsrJob job, IProgress<AsrProgress> p, CancellationToken ct);
}

public sealed record AsrCapabilities(
    TimestampPrecision Timestamps,   // Segment | Word
    PunctuationMode    Punctuation,
    string[]           Languages,
    bool               SupportsHotwords,
    bool               NeedsVad,
    bool               SupportsGpu,
    EndpointKind       Endpoint);    // InProcess | HttpLoopback（§4.5 的硬约束落在这里）
```

上层（分段、锚点、KB 投影）**只读 Capabilities，不判断引擎名**。

### 6.2 结果形状

```csharp
public sealed record AsrSegment(
    int StartMs, int EndMs, string Text,
    string? Speaker, float? Confidence, string? Lang,
    string? Engine,                  // 逐段重解时才有值，null = 跟随 run
    IReadOnlyList<AsrWord>? Words);

public sealed record AsrResult(
    string RunId, string EngineId, string ModelId,
    TimestampPrecision Timestamps,
    IReadOnlyList<AsrSegment> Segments,
    IReadOnlyList<AsrWarning> Warnings);   // LANG_SWITCH / LOW_CONF / HALLUCINATION / CHARSET_MISMATCH
```

`Warnings` 必须透传到 UI：**用户改了多少字**比"字错率"更能说明问题，而 warnings 是唯一能告诉用户"哪里该看一眼"的东西。

### 6.3 数据表

| 表 | 关键字段 | 说明 |
| --- | --- | --- |
| `mtg_transcript_run` | `id` `meeting_id` `engine` `model` `params_json` `status` `started_at` `finished_at` | 一次转写尝试；整场换引擎 = 新增 run |
| `mtg_segment` | `run_id` `start_ms` `end_ms` `text` `confidence` `speaker` `speaker_source` `lang` `engine?` `words_json?` | `engine` 非空表示这一段被单独重解过 |
| `mtg_meeting` | `current_run_id` | 当前生效的 run |

**不加 run 表，"可配置"就只是"能换，但换了不敢回头"。**

---

## 7. 性能档位

数字是估算口径，**待阶段 E 实测填真数**。

| 机器 | 引擎 | 30 分钟音频耗时 | 定位 |
| --- | --- | --- | --- |
| 8 核 CPU，无独显 | whisper turbo q8 | 估 8–15 分钟 | ✅ **默认档，必须可用** |
| 8 核 CPU，无独显 | whisper small q5 | 估 3–5 分钟 | ✅ 快速档 |
| 8 核 CPU，无独显 | Qwen3-1.7B | 估 40–90 分钟 | ⚠️ 只在"重解 3 分钟片段"时可接受 |
| 有独显 | Qwen3-1.7B | 估 5–12 分钟 | ✅ 升级档可用 |

**这个翻转最重要的收益**：v0.1 里"1.7B 在无独显上要一小时"是个产品阻断问题（得决定最低配置）；现在它只是"升级档在低配机器上只适合做逐段重解"。**风险被隔离在一个可选功能里了。**

模型体积（Q8 档）：whisper turbo **874 MB** · whisper small **190 MB** · Qwen3-ASR-1.7B GGUF **2.5 GB** · Qwen3-ASR-0.6B GGUF **1.0 GB**。默认只下载 turbo；升级档模型是按需下载，不是必装。

---

## 8. 失败与降级

| 失败 | 行为 |
| --- | --- |
| 模型未下载 | 明确提示体积 + 下载按钮；**whisper 是必装项，首次使用时引导下载** |
| sha256 不匹配 | 删包 + 报错 + 重新下载入口 |
| GPU 不可用 | 回落 CPU，UI 标注"已切 CPU，预计更慢" |
| 内存不足 | turbo → small → 明确失败并保留已转部分 |
| 单段超时 | 只重试该段 |
| 用户取消 | 保留已完成片段，run 标 `cancelled`，可续转 |
| 进程崩溃 | 启动时把 `running` 的 run 标 `interrupted`，提示"从第 N 段续转" |
| 升级档不可用 | 建议卡改成"仅供参考"的提示，不阻断 |

**"保留已完成的部分"是硬要求**：一场一小时的会转写到 90% 崩掉重来，用户会卸载软件。

---

## 9. 隐私

1. 音频默认零上传，**代码里不出现任何把音频发出本机的路径**（云端走 [asr-relay](../../../docs/contracts/asr-relay.md)，默认关闭）。
2. 转写文本也不默认外发；只有会议分析（走 Agent）会把文本发出去，且有单独确认。
3. 日志只记 `run_id / 时长 / 引擎 / 耗时`，**不记正文**。
4. 模型下载是唯一出站，且只下权重、不带任何用户数据。

---

## 10. 开发计划

> 主线是 whisper：**先让产品能转写，再谈转得更好。**
> 阶段 C 是 go / no-go 门禁；不通过就砍掉 D，产品仍然完整。

### 阶段 A · 地基（1 周）

| # | 任务 | 产物 | 验收 |
| --- | --- | --- | --- |
| A1 | 建 `SDemo.Meeting.slnf` + 五件套 + `Asr` / `Audio` 工程 | 能 build | `dotnet build SDemo.Meeting.slnf -c Release` 通过 |
| A2 | `IAsrProvider` / `AsrCapabilities` / `AsrResult` / `AsrWarning` 契约 | `Contracts` | 单测覆盖能力推导（段级 vs 字级 anchor） |
| A3 | `mtg_transcript_run` + `mtg_segment(run_id, engine?)` + `current_run_id` | `Storage` + 迁移 | Schema 迁移可重入；旧 run 可查 |
| A4 | whisper.cpp P/Invoke 接通 `large-v3-turbo q8_0` | `WhisperCppProvider` | 30 分钟中文音频转写成功，带段级时间戳 |
| A5 | 双路采集（room / online）+ 16k 单声道落盘 | `Audio` | 环回与麦克风各自成轨，时长对得上 |

**里程碑 A**：能录一场、能转一场、能存下来。

### 阶段 B · whisper 生产化（1.5 周）

| # | 任务 | 验收 |
| --- | --- | --- |
| B1 | VAD 分段 + 跨窗去重合并 | 30 分钟音频不断句错位、不重复 |
| B2 | 进度、取消、续转、中断恢复 | 取消 ≤ 2 s；中断可从第 N 段续 |
| B3 | 模型分发：下载 / sha256 / 占用显示 / 清理 | 断网可用；不匹配即删 |
| B4 | 低配档 `small` + 自动回落 | 内存不足时降档且有 UI 说明 |
| B5 | 术语表（hotwords / initial_prompt） | 产品名、客户名不被拆错 |
| B6 | 语言 hint（zh/en/ja/auto）+ 质检信号 S1–S4 产出 warnings | 混说段落在 UI 上有标记 |
| B7 | 转写结果页：段落 + 时间码 + 跳转 + 标注说话人 | 满足 MT-04 / MT-05 |

**里程碑 B**：一场真实会议能全自动跑完，失败可恢复，混说处有提示。

### 阶段 C · 升级档 spike（1 周，可与 B 并行）**← go / no-go**

| # | 任务 | 产物 |
| --- | --- | --- |
| C1 | llama.cpp server + `ggml-org/Qwen3-ASR-0.6B-GGUF:Q8_0` 跑通 | 60 秒音频经 chat-audio 形态出文本 |
| C2 | 同法跑通 `Qwen3-ASR-1.7B-GGUF`，并对照 Ollama `frozenlab/qwen3-asr` | 两种托管的 RTF / 内存 / 字错 |
| C3 | 写 `ChatAudioAsrProvider`：loopback 校验 + 固定提示 + 解析器（剥 `language` 前缀 / `<asr_text>`） | 单测覆盖三种脏输出（带前缀、纯文本、空） |
| C4 | 段落级重解可行性：只喂 30–60 秒片段 | 单段耗时 ≤ 15 秒（CPU）/ ≤ 3 秒（GPU） |
| C5 | 填 §7 真数 + 给 go/no-go | 本文档 v1.0 |

**判据**：C4 过（逐段重解在可接受时间内）→ 做 D；不过 → 砍掉升级档，S1/S4 降级为纯提示。

### 阶段 D · 升级档生产化（1.5 周，仅当 C 通过）

| # | 任务 | 验收 |
| --- | --- | --- |
| D1 | 逐段重解 + `mtg_segment.engine` 写回 | 同一条 run 内混两个引擎，KB anchor 不受影响 |
| D2 | 建议卡（§3.2）+ 一键重解 + 忽略 | 不自动执行；忽略后不再提示同段 |
| D3 | 整场重跑（信号 >40% 时） | 新建 run 并切 `current_run_id`，旧 run 可对比 |
| D4 | 降级：升级档缺模型/无 GPU 时不阻断 | 建议卡降级为提示 |

### 阶段 E · 三语基准与调优（1 周）

| # | 任务 | 验收 |
| --- | --- | --- |
| E1 | 语料：纯中 / 纯英 / 纯日 / 混说 各 10 分钟，人工校对 | 可复现的基准集 |
| E2 | whisper turbo vs small vs Qwen3-1.7B | WER、耗时、标点、时间戳偏差 |
| E3 | 校准 S1–S5 阈值 | 升级触发准确率（该升的升了、不该升的没升） |
| E4 | 片假名外来语归一化（`デプロイ` → deploy） | 检索能命中英文关键词 |
| E5 | 日语 token 密度校准 | 分析侧 map 分段按 token 自适应 |

### 阶段 F · 云端中继预留（0.5 周，可砍）

落 [asr-relay.md](../../../docs/contracts/asr-relay.md) 契约与空转 loop，不接任何厂商。产品行为零变化。

### 排期与依赖

```
A 地基 ──► B whisper 生产化 ──► E 三语基准 ──► F 云端预留(可砍)
                 │                    ▲
C 升级档 spike ──┴──► D 升级档生产化 ──┘
        (go/no-go)      (仅当 C 通过)
```

- **A → B 是主干，必须先做完**，做完产品即可用。
- C 可与 B 并行，但**结论只影响 D 做不做**，不影响主干。
- D 与 E 互不阻塞；E 的数据反过来校准 D 的阈值。

---

## 11. 风险与未决

| # | 风险 | 影响 | 应对 |
| --- | --- | --- | --- |
| R1 | 升级触发判不准（漏升/乱升） | 升级档要么没用要么烦人 | E3 校准阈值；建议式执行；S5 手动兜底 |
| R2 | Qwen3 的 ONNX 导出不可用 | 升级档做不成 | 退 OpenASR 侧车；再不行砍掉 D，产品不受影响 |
| R3 | 跨窗合并导致断句错位/重复 | 观感差 | VAD 边界对齐 + 重叠窗口去重 |
| R4 | 日语混说仍掉字 | 三语承诺打折 | 术语表 + 升级建议；UI 标注"该段可能不准" |
| R5 | 两套引擎的长期维护成本 | 版本跟不完 | 升级档不设为默认；`IAsrProvider` 隔离；旧包永远可回滚 |
| R6 | 模型总量（turbo 874MB + 可选 1.9GB） | 磁盘与下载体验 | 升级档与 ForcedAligner 全部可选，默认只下 turbo |

**未决**：

| # | 问题 | 需要谁定 |
| --- | --- | --- |
| Q1 | 升级档是否进入 P6，还是 P6 之后再说 | 产品 |
| Q2 | 转写超过 30 分钟时是否支持"隔夜转写"（关机后恢复） | 产品 |
| Q3 | 默认是否预下载 `small`（190MB，给老机器） | 产品 |
| Q4 | 云端档是否进入 P6（还是纯 P7） | 产品 |

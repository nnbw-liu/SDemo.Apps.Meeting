# sdemo.meeting · ASR 运行时

> 状态：**设计稿 v0.4（2026-11-16）· 阶段 C 已在 CPU 上证通**
> 修订：v0.2 推翻主次（whisper 主力 / Qwen3 升级档）；v0.3 修正接入形态（多模态 chat）；**v0.4 填入真机实测数，升级档默认定为 0.6B**（§7）。
> 范围：本地转写引擎、引擎升级、模型分发、分段与时间戳、性能档位、失败降级
> 关联：[asr-relay 契约](../../../docs/contracts/asr-relay.md)（云端中继，本版不启用）· [meeting-design.md](meeting-design.md) §5 · [requirements.md](requirements.md) MT-03 / MT-04 / MT-05
> 一句话：**whisper 保证"每场都能转完"，Qwen3 负责"转不准的那几段"。**

---

## 0. 一页纸结论

| 问题 | 结论 |
| --- | --- |
| **默认引擎（主力）** | **whisper.cpp `large-v3-turbo` `q8_0`**（874 MB，MIT，99 语种，段级时间戳与标点免费） |
| 低配档 | whisper `small` `q5_1`（190 MB），老机器/快速草稿 |
| **升级档（后备）** | **Qwen3-ASR-0.6B**（Apache-2.0；CPU 实测 RTF 0.20×，30 分钟音频约 6 分钟；体积 1.0 GB + mmproj 0.35 GB） |
| 升级档的大号 | Qwen3-ASR-1.7B（2.5 GB），有独显再下，默认不推 |
| 升级档接入形态 | **多模态 chat（`/v1/chat/completions` + `input_audio`），不是 `/v1/audio/transcriptions`**；llama.cpp server 实测通过，**LM Studio 不收音频（会静默丢弃）**（§4.3） |
| 升级档托管 | **随包分发 llama.cpp server**（`--mmproj` 加载音频投影），非 Ollama、非 LM Studio |
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

### 4.3 实测结果（2026-11-16，真机验证）

**结论先行：`OpenAI 兼容 ≠ 能喂音频`。这条已经用实测钉死了。**

| 运行时 | 端点 + 形状 | 结果 |
| --- | --- | --- |
| **llama.cpp server** `b12428` | `/v1/chat/completions` + `input_audio` | ✅ **可用**，中文 27 秒整段零错误（见 §7） |
| **LM Studio** 0.4.x | `/v1/chat/completions` + `input_audio` | ❌ HTTP 400：`content objects must be 'text' or 'image_url'` |
| LM Studio | `/v1/chat/completions` + `audio_url` | ❌ HTTP 400 |
| LM Studio | `/v1/chat/completions` + `images` 字段 | ⚠️ **HTTP 200，但音频被静默丢弃**：`prompt_tokens` 9→9，返回 `language None<asr_text>`（看起来像"转写成功但内容为空"） |
| LM Studio | `/api/chat`（Ollama 原生形状） | ❌ HTTP 200 + 错误体 `Unexpected endpoint (POST /api/chat)` |
| LM Studio | `/v1/audio/transcriptions`（multipart） | ❌ HTTP 415：`POST requests must use 'application/json'` |
| Ollama（社区包） | `/api/chat` + `images` | 未在本机验证（未安装）；按社区文档，单次 ≤ 60 秒 |

⚠️ **最危险的是"静默丢弃"那一行**：它不报错、返回 200、给出一个结构合法的空 ASR 输出。任何"看起来接了但没转出字"的排查，都要先做一次 token 增量探测（工具里已实现：`asr-spike probe --audio`）。

**llama.cpp 的细节**

- 官方支持 `ggml-org/Qwen3-ASR-0.6B/1.7B-GGUF`，**必须** `--mmproj` 加载音频投影，否则模型只当文本模型用。
- 启动日志出现 `audio input is in experimental stage` 才算音频链路就位。
- 音频编码器按 **8 秒窗口**预处理（[PR #19441](https://github.com/ggml-org/llama.cpp/pull/19441)）；实测 **81 秒**音频一次喂入正常出文本，没有乱码——但"一次吃 20 分钟"仍是 Qwen 官方推理框架的能力，不要按 20 分钟设计分段。
- 形态是 chat completions（mtmd），不是 transcriptions。

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

### 4.6 第三种形状：DeepSeek `/v1/tools/asr`（实测 2026-11-16）

除了"转写端点"和"多模态 chat"，还有第三种：**厂商自己的工具端点**。

| 项 | DeepSeek `POST /v1/tools/asr` |
| --- | --- |
| 载荷 | `multipart/form-data`，`file` 字段（16-bit PCM WAV 或 Ogg Opus） |
| 返回 | `{request_id, text, duration_ms}` — **纯文本，无时间戳、无分段** |
| 语种 | 无参数，自动识别（21 种） |
| 限制 | **≤ 8 MiB、≤ 120 秒**，上传 10 分钟内完成 |
| 鉴权 | Bearer API Key（云端） |

**实测结论**

- 同一段 27 秒中文音频：DeepSeek 转写**零错**（whisper turbo 错 2 处：全量→全粮、灰度→挥度），质量与 Qwen3-0.6B 同级。
- **`/v1/chat/completions` 喂音频走不通**：`input_audio` 直接 422（`unknown variant 'input_audio', expected 'text'/'image_url'/'file'`）。
  `/v1/models` 里 `deepseek-flash` 的 `input_modalities` 只有 `["text","image"]` —— **没有 audio**，这条路从模型能力上就不存在。
- 因此 DeepSeek 只能走"独立工具端点"，而且**必须先切段**（120 秒上限）。

**边界（重要）**：云端端点需要 Key，按 §4.5 的判据它属于"有凭证"→ **产品化路径必须走 Agent 中继**（[asr-relay](../../../docs/contracts/asr-relay.md) 的 `apiShape = deepseek-asr`）。
当前 Meeting CLI 里的 `deepseek-tools` 引擎是**开发/验证通路**：Key 只从 `SDEMO_ASR_API_KEY` 或命令行参数读，不落库、不进日志、不进仓；每次运行都会打印"音频将上传"的警告。

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

### 7.1 实测（2026-11-16）

**机器**：Intel i7-8700K（6 核 12 线程，2017 年桌面 CPU，无独显）· 32 GB RAM · 纯 CPU（`-ngl 0`）
**配置**：llama.cpp `b12428` · `ggml-org/Qwen3-ASR-0.6B-GGUF:Q8_0` + `mmproj-Q8_0` · `-c 8192` · `n_threads=6`
**语料**：Windows SAPI 合成（朗读腔，**不是真实会议**，见 §11 R4）

| 音频 | 时长 | 耗时 | RTF | 结果 |
| --- | --- | --- | --- | --- |
| 中文 | 27.1 s | 5.48 s（首次） / **1.98 s**（重复同段） | 0.20× / 0.07× | **102 字全部正确** |
| 英文 | 25.9 s | 5.37 s | 0.21× | 仅大小写/连字符差异 |
| 日文 | 26.6 s | 6.01 s | 0.23× | 2 处错字（`受け入れ基準`→`受け歴準`、`演習`→`演奏`） |
| 中英日混说 | 14.6 s | 2.83 s | 0.19× | ⚠️ 日文段崩塌（见 §7.3） |
| 中文长段 | 81.4 s | 18.26 s | 0.22× | 306 字，2 处错字（`全量`→`全梁`） |

- **进程内存峰值 2.13 GB**（模型 Q8 0.75 GB + mmproj 0.35 GB + KV 8192）。
- 「首次 vs 重复同一段」的差别是音频嵌入缓存，**真实场景应按 RTF 0.20×** 算（每段音频都是新的）。

**换算**：RTF 0.20× → 30 分钟会议 ≈ **6 分钟**；1 小时 ≈ **12 分钟**。这比 v0.2 的估算（0.6B 15–25 分钟）好一倍。

### 7.2 结论：0.6B 在纯 CPU 上已经够用

这条实测推翻了本文档 v0.1/v0.2 的一个前提——"1.7B 在无独显上要一小时"。实际情况是：

| 机器 | 引擎 | 30 分钟音频 | 定位 |
| --- | --- | --- | --- |
| 6 核桌面 CPU，无独显 | **Qwen3-ASR-0.6B Q8** | **≈ 6 分钟（实测 RTF 0.20）** | ✅ 升级档主力 |
| 同上 | **whisper turbo q8（Whisper.net）** | **≈ 20 分钟（实测 RTF 0.65，6 线程）** | ⚠️ 主力档，但**比 Qwen3 慢 3 倍** |
| 同上 | whisper small q5 | 估 8–10 分钟（待实测） | ✅ 快速档 |
| 同上 | Qwen3-ASR-1.7B Q8 | 估 15–25 分钟（0.6B 的 ~2.5 倍） | ⚠️ 待实测 |
| 有独显 | 任意 | 快 3–10 倍 | ✅ |

⚠️ **实测推翻了"whisper 更快"的直觉**：同一台机器、同一段 27 秒中文音频，whisper turbo 用 18.9 s（含 1.4 s 模型加载，RTF≈0.65），Qwen3-ASR-0.6B 用 5.5 s（RTF 0.20）。**纯 CPU 上 Qwen3 快 3 倍，且字错更少**（whisper 把"全量"听成"全粮"、"灰度"听成"挥度"；Qwen3 同一段零错）。

这不是小差距，是"主力档该不该是 whisper"的产品问题。当前仍按 §0 的主次执行（whisper 主力），但**建议在阶段 B 末尾用真会议语料复核一次主次**——如果结论稳定，就把两者对调，成本只是换默认档。

**产品含义**：升级档不再是"只有独显用户能用"。**默认升级档建议用 0.6B（1.0 GB），1.7B 作为"有独显再下"的可选包**——这和"体积/收益"的直觉一致，也和实测速度一致。

### 7.4 音频的 token 成本（这条直接决定分段上限）

实测 `prompt_tokens` 与音频时长成正比，约 **13.7 token / 秒**（19.7 s → 270，27.1 s → 374）：

| 音频 | 音频 token | 8K 上下文（`-c 8192`）能否装下 |
| --- | --- | --- |
| 30 s | ≈ 410 | ✅ |
| 60 s | ≈ 820 | ✅ |
| 5 min | ≈ 4,100 | ✅ |
| 10 min | ≈ 8,200 | ❌ 超 |
| 30 min | ≈ 24,700 | ❌ 需 ≥ 32K |
| 60 min | ≈ 49,000 | ❌ 需 ≥ 64K |

**所以"官方支持一次 20 分钟"在小上下文的本地部署上不成立。** 结论：**按 20–60 秒分段送**（每段 ≈ 270–820 token，安全且失败只丢一小段），这与 §4.4 的分段策略一致，也和 whisper 的 30 秒窗口处理习惯一致。

另外实测 `prompt_ms ≈ 9.2 ms/token`，**音频编码（prompt）占总耗时约 2/3，解码只占 1/3**——所以提速的第一手段是减少重复编码（分段不要重叠太多），不是换更快的解码器。

### 7.5 混说样本为什么不作数

测试用的"混说"是让中文 SAPI 读一段夹着日文的文本——等于要求模型听懂"中国人用中文音系念日文"，这在真实会议里不存在。它暴露的是**合成语料的失真**，不是模型的缺陷。

必须补的真语料（阶段 E）：母语者分别录中/英/日，以及**同一个人在同一次发言里自然切换语言**。这条在 §11 R4 已记为风险。

模型体积（Q8 档）：whisper turbo **874 MB** · whisper small **190 MB** · Qwen3-ASR-1.7B GGUF **2.5 GB** · **Qwen3-ASR-0.6B GGUF 1.0 GB（+ mmproj 0.35 GB）**。默认只下载 turbo；升级档模型是按需下载，不是必装。

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

| # | 任务 | 状态 |
| --- | --- | --- |
| A1 | 建 `SDemo.Meeting.slnf` + 五件套 + `Asr` / `Audio` 工程 | ✅ **2026-11-16 完成**：8 个工程，`dotnet build SDemo.Meeting.slnf -c Release` 0 警告 |
| A2 | `IAsrProvider` / `AsrCapabilities` / `AsrResult` / `AsrWarning` 契约 | ✅ 完成；`Anchor` 能力推导有单测（None→Document / Segment→Sentence / Word→Block） |
| A3 | `mtg_transcript_run` + `mtg_segment(run_id, engine?)` + `current_run_id` | ✅ 完成；schema v1 + `user_version` 迁移可重入；软删、interrupted 恢复、序号唯一约束都有测试 |
| A4 | whisper.cpp 接通 `large-v3-turbo q8_0` | ✅ 完成；**P/Invoke 用 Whisper.net 1.8.1**（同一份 ggml-org 原生库，ABI 由上游维护）；真机 27.1s 中文 → 9 段带时间戳，落库可回读 |
| A5 | 双路采集（room / online）+ 16k 单声道落盘 | ⚠️ 代码完成，**本机被 Windows 麦克风隐私拒绝**（0x80070005），0.4 秒内明确失败并给出"设置→隐私→麦克风"提示；换有权限的机器即可验证 |

**里程碑 A**：能录一场、能转一场、能存下来 —— 除 A5 的硬件权限外，`import → transcribe → list/show` 已端到端跑通。

**实现备注（A4）**：没有自己手写 `whisper_full_params` 的 P/Invoke 结构体——实测 `whisper_full_default_params` 按值返回结构体，P/Invoke 按 `IntPtr` 接会直接崩（0xC0000005）。改用 Whisper.net 的绑定后，同一个 `whisper.dll` + 同一个模型文件，行为一致且不用赌 ABI 版本。

### 阶段 B · whisper 生产化（1.5 周）

| # | 任务 | 状态 |
| --- | --- | --- |
| B1 | VAD 分段 + 跨窗去重合并 | ✅ **2026-11-16 完成**：能量型 VAD（无模型、20 ms 帧、双阈值迟滞）+ **窗口打包**；81 秒真实音频 → 2 个窗口（0 s / 57 s），不再是 27 个句子级碎片 |
| B2 | 进度、取消、续转、中断恢复 | ✅ 完成：增量落库（取消/崩溃保留已完成段）、`resume --run` 从第 N 段续、启动时 `running → interrupted`；测试覆盖取消与续转 |
| B3 | 模型分发：下载 / sha256 / 占用显示 / 清理 | ✅ **2026-11-16 完成**：权重不进 git，两条来源——安装包自带（`tools/pack-models.ps1`）+ 用户导入（`meeting models --import`）；`models.json` 记 sha256；`--verify` 重算、`--remove` 清理、列表显示占用与状态 |
| B4 | 低配档 `small` + 自动回落 | ✅ **2026-11-16 完成**：`AsrTierSelector` 事前按可用内存选档（模型体积 + 512 MB 余量），`FallbackAsrProvider` 事中捕获 OOM 再降一次；降档说明以 `EngineFallback` 告警落到段上、`show` 里可见 |
| B5 | 术语表（hotwords / initial_prompt） | ✅ 已接：CLI `--hotwords "SDemo,Qwen3"` → whisper `WithPrompt`，参数落 `params_json` |
| B6 | 语言 hint + 质检信号 S1–S4 | ✅ 已接：`--lang` 固定语言；`TranscriptQuality` 产出 warnings 并按时间回写到 `mtg_segment.warnings`；`show` 里以 ⚠ 显示 |
| B7 | 转写结果页（段落 + 时间码 + 跳转 + 说话人） | ⬜ 未做（需要 WPF 界面，CLI 只能 `show` 打印） |

**B1 的关键修正（实测踩出来的）**：VAD 在自然停顿处会把音频切成"一句话一段"——81 秒音频切出 **27 段**，等于 27 次模型调用、27 次音频编码，慢且丢上下文。所以加了一层**窗口打包**：相邻语音段若停顿 ≤4 秒且总长 ≤60 秒就并回一个窗口。加完同一段音频变成 **2 个窗口**，总耗时 19.4 秒（RTF 0.24）。

**B3 的形态（权重不进 git）**

| 来源 | 怎么做 | 谁用 |
| --- | --- | --- |
| **安装包自带** | `pwsh tools/pack-models.ps1 -SourceDir <有模型的地方>` → 校验 sha256 → 复制到 `artifacts/models` + 写 `models.json` | 默认档（whisper-turbo-q8），开箱即用 |
| **用户导入** | `meeting models --import <文件> [--id <packId>]` | 低配 `small`、升级档 Qwen3、自定义模型 |
| **体检 / 清理** | `meeting models --verify <id>` / `--remove <id>` | 下载损坏、换硬盘、腾空间 |

`models.json` 与运行期 manifest 同格式，所以安装包解到数据目录后**直接就是"已就绪"**。实测：真实 874 MB 模型的 sha256 `317eb69c…` 与内置常量一致，`--verify` 3.6 秒算完。

**里程碑 B**：一场真实会议能全自动跑完，失败可恢复，混说处有提示。—— B1/B2/B3/B4/B5/B6 已就位；B7（结果页）未做。

**B4 的两道闸**

| 时机 | 谁 | 判据 | 用户看到什么 |
| --- | --- | --- | --- |
| 事前 | `AsrTierSelector.Choose` | 可用物理内存 < 模型体积 + 512 MB | `doctor` 显示"档位决策：small（降档）"；转写前打印一条 warn |
| 事中 | `FallbackAsrProvider` | 引擎抛 OOM / `bad_alloc` / `failed to allocate` | 自动换成 small 重试一次；结果首段带 `EngineFallback` 告警，`show` 里显示 ⚠ |

边界：**非内存错误（模型损坏、音频读不了）不降档**——换档救不了，只会把真错误盖住；低配档也没装时明确报错，不静默用更差的档。

### 阶段 C · 升级档 spike（1 周，可与 B 并行）**← 已部分完成，判定通过**

| # | 任务 | 状态 |
| --- | --- | --- |
| C1 | llama.cpp server + `ggml-org/Qwen3-ASR-0.6B-GGUF:Q8_0` 跑通 | ✅ **2026-11-16 完成**：中文 27 秒整段零错误 |
| C2 | `Qwen3-ASR-1.7B-GGUF` 与 Ollama 社区包对照 | ⬜ 未做（1.7B 未下载；Ollama 未安装） |
| C3 | `ChatAudioAsrProvider`：loopback + 固定提示 + 解析器 | ✅ 解析器完成（`asr-spike parse --self-test` 9/9）；provider 未落地 |
| C4 | 段落级重解可行性：只喂 30–60 秒片段 | ✅ **通过**：27 秒 5.48 s（首次），阈值是 ≤ 15 s |
| C5 | 填 §7 真数 + 给 go/no-go | ⚠️ 0.6B/CPU 已填；1.7B、GPU、whisper 三行仍是估算 |

**判定：go。** C4 在纯 CPU 上以 3 倍余量通过，升级档不再是"只有独显能用"。

剩余待补（可延后，不阻塞 D）：1.7B 的数字、GPU 档、whisper turbo/small 的实测对照（属阶段 E）。

⚠️ **C 阶段新增一条前置检查（本次实测的教训）**：接任何后端前，先跑 `asr-spike probe --model <名> --audio`。它会比对"带音频 / 不带音频"的 `prompt_tokens`，**专门捉住"服务收下了但把音频静默丢弃"这种情况**（LM Studio 就是这样：返回 200 + 空转写，看起来一切正常）。

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

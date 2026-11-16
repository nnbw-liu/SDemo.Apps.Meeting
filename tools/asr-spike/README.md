# asr-spike —— Qwen3-ASR 的 chat-audio 接入验证

对应 [asr-runtime.md](../../docs/asr-runtime.md) 的**阶段 C（升级档 spike，go / no-go）**。
目的只有一个：**用真音频、真机器，把 `Qwen3-ASR` 在"chat-audio 形态"下能不能用、多快，测出来。**

它不是一个可以上线的组件：不落库、不并发、不做 VAD、不做重试策略。测完就丢，结论进 `asr-runtime.md` §7。

---

## 0. 已验证的结论（2026-11-16）

| 后端 | 形状 | 结果 |
| --- | --- | --- |
| **llama.cpp server** | `--shape llama`（`input_audio`） | ✅ 可用。中文 27 秒 **零错**，RTF 0.20× |
| **LM Studio** | `--shape llama` | ❌ 400：只接受 `text` / `image_url` |
| **LM Studio** | `--shape lms`（`images` 字段） | ⚠️ **200 + 静默丢弃音频**，返回 `language None<asr_text>` |
| **LM Studio** | `--shape ollama`（`/api/chat`） | ❌ 200 + 错误体 `Unexpected endpoint` |
| LM Studio | `/v1/audio/transcriptions` | ❌ 415（只收 JSON） |

**不要用 LM Studio 跑 ASR**。它不会报错，只会给你一段"看起来成功"的空转写。
先用 `probe --audio` 验证：它比对带/不带音频的 `prompt_tokens`，能捉住静默丢弃。

---

## 1. 前置

| 项 | 说明 |
| --- | --- |
| .NET 10 SDK | `dotnet build` 能过即可 |
| **ffmpeg**（强烈建议） | 负责转 16 kHz 单声道 + 切片段。**不在 PATH 里也能用 `--ffmpeg <路径>`**；完全没有时只接受已合规的 16 kHz WAV，且不能切片段 |
| 模型 | **llama.cpp 的 GGUF + mmproj**（推荐）；Ollama 社区包次之 |

> 当前这台机器 PATH 里没有 ffmpeg。要么 `winget install Gyan.FFmpeg`，要么用 `make-test-audio.ps1` 直接产出 16 kHz WAV。

### 没有真会议录音时：合成测试音频

```powershell
pwsh ./make-test-audio.ps1          # 生成 zh/en/ja/mixed 四段到 ./samples
pwsh ./make-test-audio.ps1 -ListVoices
```

它用 Windows SAPI 合成 16 kHz / 单声道 / 16-bit WAV。**朗读腔不等于远场多人会议**，只用于验证链路与速度；WER 必须用真录音另测（asr-runtime §11 R4）。

---

## 2. 起后端

### ① llama.cpp server（**唯一验证可用的路径**）

不需要完整源码，只要官方 release 的 `llama-server.exe`（windows-cpu-x64 包约 24 MB），
模型可以直接用 LM Studio 已经下好的那两个文件：

```powershell
llama-server -m "C:\Users\<你>\.lmstudio\models\ggml-org\Qwen3-ASR-0.6B-GGUF\Qwen3-ASR-0.6B-Q8_0.gguf" `
             --mmproj "C:\Users\<你>\.lmstudio\models\ggml-org\Qwen3-ASR-0.6B-GGUF\mmproj-Qwen3-ASR-0.6B-Q8_0.gguf" `
             --host 127.0.0.1 --port 8080 --jinja -ngl 0 -c 8192
```

启动日志里必须出现这两行，否则音频链路没起来：

```
init_audio: audio input is in experimental stage ...
load_model: loaded multimodal model, '...mmproj-....gguf'
```

### ② Ollama（社区包，未在本机验证；单次 ≤ 60 秒）

```powershell
ollama pull frozenlab/qwen3-asr:1.7b     # 约 2.5 GB
ollama serve                              # 默认 127.0.0.1:11434
```

### ✗ LM Studio（**不行**，见 §0）

---

## 3. 命令

```powershell
# 起了没、有什么模型
asr-spike probe --shape llama --base-url http://127.0.0.1:8080

# ★ 接新后端第一步：确认它真的收了音频（比对 prompt_tokens 增量）
asr-spike probe --shape llama --base-url http://127.0.0.1:8080 --model qwen3-asr --audio

# 只验证请求形状与体积，不发请求
asr-spike run --input 会议.m4a --start 00:12:31 --duration 60 --model qwen3-asr --dry-run

# 真跑一次
asr-spike run --shape llama --base-url http://127.0.0.1:8080 --model qwen3-asr --input samples\zh-60s.wav --prompt "" --save-raw raw.json

# 测速（预热 1 次 + 测 3 次，首次含模型加载，必须预热）
asr-spike bench --input samples\zh-60s.wav --model qwen3-asr --repeat 3 --warmup 1 --out bench.md

# 解析器自测（不需要模型）
asr-spike parse --self-test
```

### 关键参数

| 参数 | 说明 |
| --- | --- |
| `--shape` | `llama`/`openai` → `input_audio`；`ollama` → `/api/chat`+`images`；`lms` → `/v1/chat/completions`+`images`（用来复现 LM Studio 的静默丢弃） |
| `--duration` | 建议 **≤ 60 秒**（升级档分段目标；实测 81 秒也能跑） |
| `--warmup` | 默认 1。第一次调用包含模型加载，不预热会把首次耗时算进去 |
| `--out` | 追加一行 Markdown，直接贴进 `asr-runtime.md` §7 |
| `--prompt` | Qwen3-ASR 官方用法是**空 prompt**；LM Studio 之类严格服务会拒空 text |

退出码：`0` 成功 · `1` 参数/IO 错误 · `2` HTTP/连接失败 · `3` 解析出空文本 · **`4` 音频没进模型（静默丢弃）**

---

## 4. 该测什么（对应阶段 C）

| 阶段 C 任务 | 怎么测 | 记录 |
| --- | --- | --- |
| C1 0.6B 跑通 | `run --model ...0.6b` 一段 60 秒 | 能不能出文本 |
| C2 1.7B + 两种托管对照 | 同音频分别跑 llama.cpp 与 Ollama | RTF、内存、字错 |
| C3 解析器 | `parse --self-test` + 真实输出 | 前缀/纯文本/空 三种脏输出 |
| C4 **逐段重解可行性** | `bench --duration 30` | **单段 ≤ 15 s（CPU）/ ≤ 3 s（GPU）才算过** |
| C5 填数 | `--out bench.md` | 贴回 §7，给 go/no-go |

### 测试素材建议

| 片段 | 用途 |
| --- | --- |
| 纯中文 60 秒 | 基线 |
| 纯日文 60 秒 | 验证日语（片假名外来语、同音词） |
| 纯英文 60 秒 | 基线 |
| **中英日混说 60 秒** | **最关键**——whisper 在这一项上会掉字，升级档的价值全看这一条 |

---

## 5. 排错

| 现象 | 原因 |
| --- | --- |
| `404` | llama.cpp 模型没加载；或 Ollama 模型名写错（`ollama list`）；或 `--shape` 与后端不匹配 |
| `400` | 音频字段形状不对；或超过单次上限（Ollama 约 60 秒） |
| `415` / `422` | 不是 16 kHz 单声道 16-bit WAV |
| 输出以 `language xx` 开头 | 正常，解析器会剥掉；`--raw` 能看到原样 |
| 输出是乱码/重复 | **典型信号**：llama.cpp 没按 8 秒窗口预处理（版本太旧），或音频被上游当成图片 |
| 首次调用几十秒 | 模型加载。用 `--warmup 1`（默认）排除 |

---

## 6. 已知边界

- 不落库、不并发、不重试——这些是阶段 D 的事。
- `--shape ollama` 用的是 Ollama 私有 `/api/chat` + `images` 字段，**不是 OpenAI 标准**；换成 llama.cpp 就是 `input_audio`。两者都要支持，因为用户可能只装了其中之一。
- 不做说话人分离、不做时间戳对齐：**时间戳由我们自己的 VAD 切段边界提供**（见 asr-runtime §5.1）。

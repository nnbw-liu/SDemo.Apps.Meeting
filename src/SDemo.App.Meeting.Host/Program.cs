using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using SDemo.App.Meeting.Asr;
using SDemo.App.Meeting.Audio;
using SDemo.App.Meeting.Contracts;
using SDemo.App.Meeting.Core;
using SDemo.App.Meeting.Mcp;
using SDemo.App.Meeting.Storage;

namespace SDemo.App.Meeting.Host;

/// <summary>
/// 双模入口：无参数 → WPF 主窗口（进程内起 MCP）；带参数 → CLI（给脚本/CI 用）。
/// 主窗口是产品形态，CLI 是调试与自动化的旁路，两者共用 <see cref="MeetingHost"/>。
/// </summary>
public static class Program
{
    private const string MutexName = "SDemo.App.sdemo.meeting";
    private const uint AttachParentProcess = 0xFFFFFFFF;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint processId);

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length == 0 || args[0].Equals("--gui", StringComparison.OrdinalIgnoreCase))
            return RunGui(Options.Parse(args.Length == 0 ? new[] { "app" } : args));

        // WinExe 默认没有控制台；接回调用方的终端，否则 CLI 输出会消失
        AttachConsole(AttachParentProcess);
        return RunCliAsync(args).GetAwaiter().GetResult();
    }

    private static int RunGui(Options options)
    {
        var instance = options.Str("instance");
        var mutexName = instance is null ? MutexName : $"{MutexName}.{instance}";

        using var mutex = new Mutex(initiallyOwned: true, mutexName, out var createdNew);
        if (!createdNew)
        {
            // 不弹框：Hub 可能重复拉起，弹窗会卡住自动启动；日志留痕即可
            Log($"已有实例在运行（{mutexName}），第二个进程退出");
            return 0;
        }

        MeetingHost? host = null;
        try
        {
            var dataDir = options.Str("data-dir");
            Log($"启动：打开数据目录（{dataDir ?? "默认"}）");
            host = MeetingHost.Open(dataDir);
            var services = host.BuildServices();
            var port = host.StartMcp(port: options.Int("port", 0));
            Log($"MCP 已在进程内启动：127.0.0.1:{port}");

            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            var window = new MainWindow(services);
            app.MainWindow = window;
            Log("窗口已创建，进入消息循环");

            // 窗口关掉就退出；MCP 由 finally 里的 host.Dispose 收尾（runtime 文件转 stopped）
            var code = app.Run(window);
            Log($"消息循环结束 code={code}");
            return code;
        }
        catch (Exception ex)
        {
            Log($"启动失败：{ex}");
            MessageBox.Show($"会议启动失败：\n{ex.Message}", "会议", MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }
        finally
        {
            host?.Dispose();
            Log("已释放组合根");
        }
    }

    internal static void Log(string message)
    {
        try
        {
            var dir = MeetingConstants.ResolveDataDir();
            Directory.CreateDirectory(dir);
            File.AppendAllText(
                Path.Combine(dir, "meeting.log"),
                $"{DateTimeOffset.Now:MM-dd HH:mm:ss.fff} [{Environment.ProcessId}] {message}{Environment.NewLine}");
        }
        catch
        {
            // 日志失败不能影响主流程
        }
    }

    public static async Task<int> RunCliAsync(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { /* 部分终端不支持 */ }

        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Help();
            return 0;
        }

        var cmd = args[0].ToLowerInvariant();
        var o = Options.Parse(args);

        try
        {
            using var app = MeetingHost.Open(o.Str("data-dir"));

            return cmd switch
            {
                "record" => await RecordAsync(app, o),
                "import" => await ImportAsync(app, o),
                "transcribe" => await TranscribeAsync(app, o),
                "resume" => await ResumeAsync(app, o),
                "list" => List(app, o),
                "show" => Show(app, o),
                "models" => Models(app, o),
                "doctor" => Doctor(app),
                "tools" => Tools(),
                "audit" => Audit(app, o),
                "serve" => await ServeAsync(app, o),
                _ => Unknown(cmd),
            };
        }
        catch (MeetingException ex)
        {
            Console.Error.WriteLine($"[{ex.Code}] {ex.Message}");
            return ex.Code == MeetingErrorCodes.InvalidArgument ? 1 : 3;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[error] {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    // ── record ──────────────────────────────────────────────────────────

    private static async Task<int> RecordAsync(MeetingHost app, Options o)
    {
        var title = o.Str("title") ?? $"会议 {DateTimeOffset.Now:MM-dd HH:mm}";
        var seconds = o.Int("seconds", 0);
        var includeLoopback = !o.Flag("no-loopback");
        var dir = o.Str("dir") ?? Path.Combine(MeetingConstants.ResolveDataDir(), MeetingConstants.RecordingsDirName);

        if (!DualTrackRecorder.HasCaptureDevice())
        {
            Console.Error.WriteLine($"[{MeetingErrorCodes.NoAudioDevice}] 本机没有可用的录音设备（麦克风/环回）");
            return 3;
        }

        var meeting = app.Meetings.CreateMeeting(title, ParseKind(o.Str("kind")));
        Console.WriteLine($"会议已建：{meeting.Id}  {meeting.Title}");

        var recorder = new DualTrackRecorder(Path.Combine(dir, meeting.Id), "audio");
        recorder.Start(includeLoopback);
        Console.WriteLine(seconds > 0 ? $"录音中… {seconds} 秒（Ctrl+C 可提前结束）" : "录音中… 按 Enter 结束");

        using var cts = new CancellationTokenSource();
        var stopTask = seconds > 0
            ? Task.Delay(TimeSpan.FromSeconds(seconds), cts.Token)
            : Task.Run(() => { Console.ReadLine(); }, cts.Token);

        try
        {
            await stopTask;
        }
        catch (OperationCanceledException)
        {
            // 忽略
        }

        var recording = recorder.Stop();
        Console.WriteLine($"录音结束：{recording.StoppedAt - recording.StartedAt:hh\\:mm\\:ss}");

        foreach (var track in recording.Tracks)
        {
            var asset = app.Meetings.AttachAudio(meeting.Id, track.Source, track.TargetPath);
            Console.WriteLine($"  {track.Source,-10} {asset.DurationMs / 1000.0,8:F1}s  {asset.Path}");
        }

        Console.WriteLine();
        Console.WriteLine($"下一步：meeting transcribe --meeting {meeting.Id}");
        return 0;
    }

    // ── import ──────────────────────────────────────────────────────────

    /// <summary>导入已有录音（没有麦克风、或测试链路时用）。</summary>
    private static async Task<int> ImportAsync(MeetingHost app, Options o)
    {
        var audioPath = o.Str("audio") ?? throw MeetingException.Invalid("缺少 --audio");
        if (!File.Exists(audioPath)) throw new MeetingException(MeetingErrorCodes.AudioNotFound, $"音频不存在：{audioPath}");

        var title = o.Str("title") ?? Path.GetFileNameWithoutExtension(audioPath);
        var meeting = app.Meetings.CreateMeeting(title, ParseKind(o.Str("kind")));
        var asset = app.Meetings.AttachAudio(meeting.Id, AudioSource.Microphone, audioPath);

        Console.WriteLine($"已导入：{meeting.Id}  {asset.DurationMs / 1000.0:F1}s  {asset.Path}");

        if (!o.Flag("transcribe")) return 0;

        o.Set("meeting", meeting.Id);
        o.Set("audio", audioPath);
        return await TranscribeAsync(app, o);
    }

    // ── transcribe ──────────────────────────────────────────────────────

    private static async Task<int> TranscribeAsync(MeetingHost app, Options o)
    {
        var meetingId = o.Str("meeting") ?? throw MeetingException.Invalid("缺少 --meeting");
        var meeting = app.Meetings.RequireMeeting(meetingId);

        var engineId = o.Str("engine", AsrEngineCatalog.Whisper)!;
        var language = o.Str("lang", "zh");
        var audioPath = o.Str("audio");

        if (audioPath is null)
        {
            var assets = app.Session.ListAudio(meetingId);
            if (assets.Count == 0) throw new MeetingException(MeetingErrorCodes.AudioNotFound, "这场会议没有音频");

            // 优先麦克风轨：它含"自己"，环回只含对方；后续阶段按需合并两轨
            var preferred = assets.FirstOrDefault(a => a.Source == AudioSource.Microphone) ?? assets[0];
            audioPath = preferred.Path;
            Console.WriteLine($"使用音频：{preferred.Source}  {preferred.Path}");
        }

        using var provider = CreateProvider(app, o, engineId);

        if (provider is IFallbackNote { Note: { Length: > 0 } note })
            Console.Error.WriteLine($"[warn] {note}");

        if (provider is ChatAudioProvider chat && !chat.IsLocal)
        {
            Console.Error.WriteLine($"[warn] 音频将上传到 {o.Str("base-url")}（云端例外，按场计费）");
        }

        if (provider is DeepSeekToolsProvider)
        {
            Console.Error.WriteLine($"[warn] 音频将上传到 {o.Str("base-url", DeepSeekToolsProvider.DefaultBaseUrl)}（云端例外，按音频小时计费）");
        }

        var availability = provider.Probe();
        if (!availability.Available)
        {
            Console.Error.WriteLine($"[{MeetingErrorCodes.AsrUnavailable}] {availability.Reason} {availability.Detail}");
            return 3;
        }

        var progress = new Progress<AsrProgress>(p =>
        {
            if (p.Percent % 20 < 5) Console.WriteLine($"  … {p.Percent}%  {p.Stage}");
        });

        var options = BuildOptions(o, language);

        var runner = new TranscriptionRunner(app.Meetings);
        var slice = await runner.RunAsync(
            meeting.Id, audioPath, provider, options,
            progress: progress,
            ct: CancellationToken.None);

        Console.WriteLine();
        Console.WriteLine($"转写完成：{slice.Run.Engine} · {slice.Segments.Count} 段 · {slice.TotalChars} 字");
        foreach (var seg in slice.Segments)
            Console.WriteLine($"  [{seg.FormatTimecode()}] {seg.Text}");

        return 0;
    }

    // ── resume ──────────────────────────────────────────────────────────

    /// <summary>续转：从已落库的段数继续，不重跑已完成部分。</summary>
    private static async Task<int> ResumeAsync(MeetingHost app, Options o)
    {
        var runId = o.Str("run") ?? throw MeetingException.Invalid("缺少 --run <runId>");
        var run = app.Meetings.RequireRun(runId);

        if (run.Status is TranscriptRunStatus.Done or TranscriptRunStatus.Failed)
            throw new MeetingException(MeetingErrorCodes.RunNotTerminal, $"run 已是终态（{run.Status}），不能续转");

        var audioPath = o.Str("audio");
        if (audioPath is null)
        {
            var asset = run.AudioAssetId is { } id ? app.Session.GetAudio(id) : null;
            audioPath = asset?.Path
                        ?? throw new MeetingException(MeetingErrorCodes.AudioNotFound, "找不到这次 run 的音频，请用 --audio 指定");
        }

        var engineId = o.Str("engine") ?? run.Engine;
        using var provider = CreateProvider(app, o, engineId);

        Console.WriteLine($"续转 run {runId}：已完成 {app.Meetings.CountSegments(runId)} 段");

        var runner = new TranscriptionRunner(app.Meetings);
        var slice = await runner.ResumeAsync(
            runId, audioPath, provider,
            BuildOptions(o, o.Str("lang", "zh")),
            new Progress<AsrProgress>(p => Console.WriteLine($"  … {p.Percent}%  {p.Stage}")),
            CancellationToken.None);

        Console.WriteLine($"续转完成：{slice.Segments.Count} 段 · {slice.TotalChars} 字");
        return 0;
    }

    /// <summary>按引擎造 provider；whisper 走"带降档"的工厂（B4）。</summary>
    private static IAsrProvider CreateProvider(MeetingHost app, Options o, string engineId)
    {
        var apiKey = o.Str("api-key") ?? Environment.GetEnvironmentVariable(AsrEngineCatalog.ApiKeyEnvVar);

        var isWhisper = engineId.Equals(AsrEngineCatalog.Whisper, StringComparison.OrdinalIgnoreCase)
                        || engineId.Equals("whisper", StringComparison.OrdinalIgnoreCase)
                        || engineId.Equals("whispercpp", StringComparison.OrdinalIgnoreCase);

        return isWhisper
            ? AsrEngineCatalog.CreateWhisperWithFallback(app.ModelCatalog, o.Str("whisper-model"))
            : AsrEngineCatalog.Create(engineId, app.ModelCatalog, o.Str("base-url"), o.Str("model"),
                o.Str("whisper-model"), apiKey, o.Flag("allow-remote"));
    }

    private static TranscriptionOptions BuildOptions(Options o, string? language) =>        new(
            Language: language,
            Hotwords: ParseList(o.Str("hotwords")),
            MaxSegmentMs: o.Int("max-segment", 60_000),
            UseVad: !o.Flag("no-vad"));

    private static IReadOnlyList<string>? ParseList(string? raw) =>
        string.IsNullOrWhiteSpace(raw)
            ? null
            : raw.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    // ── 只读命令 ────────────────────────────────────────────────────────

    private static int List(MeetingHost app, Options o)
    {
        var meetings = app.Meetings.ListMeetings(o.Int("limit", 20));
        if (meetings.Count == 0)
        {
            Console.WriteLine("还没有会议。用 `meeting record --title ... --seconds 10` 录一场。");
            return 0;
        }

        foreach (var m in meetings)
        {
            var runs = app.Session.ListRuns(m.Id);
            var current = runs.FirstOrDefault(r => r.Id == m.CurrentRunId);
            Console.WriteLine(
                $"{m.Id}  {m.StartAt.ToLocalTime():MM-dd HH:mm}  {m.Status,-11}  " +
                $"{(current is null ? "未转写" : $"{current.Engine}/{current.Status}"),-22}  {m.Title}");
        }

        return 0;
    }

    private static int Show(MeetingHost app, Options o)
    {
        var id = o.Str("meeting") ?? o.Positional.FirstOrDefault() ?? throw MeetingException.Invalid("缺少会议 id");
        var meeting = app.Meetings.RequireMeeting(id);

        Console.WriteLine($"{meeting.Title}  ({meeting.Kind})");
        Console.WriteLine($"  id      : {meeting.Id}");
        Console.WriteLine($"  uri     : {meeting.Uri}");
        Console.WriteLine($"  start   : {meeting.StartAt.ToLocalTime():yyyy-MM-dd HH:mm}");
        Console.WriteLine($"  status  : {meeting.Status}");
        Console.WriteLine($"  current : {meeting.CurrentRunId ?? "-"}");

        foreach (var audio in app.Session.ListAudio(id))
            Console.WriteLine($"  audio   : {audio.Source,-10} {audio.DurationMs / 1000.0:F1}s  {audio.Path}");

        foreach (var run in app.Session.ListRuns(id))
            Console.WriteLine($"  run     : {run.Id}  {run.Engine}/{run.Model}  {run.Status}  {run.StartedAt.ToLocalTime():MM-dd HH:mm:ss}");

        if (app.Meetings.GetCurrentTranscript(id) is { } slice)
        {
            Console.WriteLine();
            foreach (var seg in slice.Segments)
            {
                var flag = seg.Warnings.Count == 0 ? "" : $"  ⚠ {string.Join(",", seg.Warnings)}";
                Console.WriteLine($"  [{seg.FormatTimecode()}] {seg.Text}{flag}");
            }
        }

        return 0;
    }

    private static int Models(MeetingHost app, Options o)
    {
        var store = app.ModelCatalog.Store;

        if (o.Str("import") is { } importPath)
        {
            var result = store.Import(importPath, o.Str("id"), o.Flag("overwrite"));
            Console.WriteLine($"已导入 {result.Definition.Id} → {result.Path}");
            Console.WriteLine($"  {result.Bytes / 1024d / 1024d:F1} MB · sha256 {result.Sha256[..16]}…{(result.Overwrote ? "（已覆盖）" : "")}");
            return 0;
        }

        if (o.Str("remove") is { } removeId)
        {
            var removed = store.Remove(removeId);
            Console.WriteLine(removed ? $"已移除 {removeId}" : $"{removeId} 未安装，无需移除");
            return 0;
        }

        if (o.Str("verify") is { } verifyId)
        {
            var model = store.Verify(verifyId);
            Console.WriteLine($"{model.Definition.Id,-22} {model.Status,-14} {model.ActualSha256?[..16]}…");
            return model.IsReady ? 0 : 3;
        }

        Console.WriteLine($"模型目录：{store.ModelDir}");
        Console.WriteLine($"总占用  ：{store.TotalBytes() / 1024d / 1024d / 1024d:F2} GB");
        Console.WriteLine();
        Console.WriteLine($"{"id",-22} {"档位",-8} {"状态",-14} {"大小",10}  文件");

        foreach (var model in store.List())
        {
            var size = model.Status == ModelStatus.Missing
                ? model.Definition.ExpectedSizeText
                : $"{model.ActualBytes / 1024d / 1024d:F0} MB";

            Console.WriteLine($"{model.Definition.Id,-22} {model.Definition.Tier,-8} {model.Status,-14} {size,10}  {model.Definition.FileName}");
        }

        Console.WriteLine();
        Console.WriteLine("导入自己的模型：meeting models --import <文件> [--id <packId>]");
        Console.WriteLine("校验 / 移除   ：meeting models --verify <packId> | --remove <packId>");
        return 0;
    }

    private static int Doctor(MeetingHost app)
    {
        Console.WriteLine($"数据目录 : {MeetingConstants.ResolveDataDir()}");
        Console.WriteLine($"数据库   : {app.Database.Path}");
        Console.WriteLine($"录音设备 : {(DualTrackRecorder.HasCaptureDevice() ? "有" : "无")}");

        var memory = SystemMemory.Read();
        Console.WriteLine($"内存     : 可用 {memory.AvailableGb:F1} GB / 共 {memory.TotalGb:F1} GB");

        var decision = AsrTierSelector.Choose(
            app.ModelCatalog.Store,
            ModelPacks.WhisperTurboQ8,
            memory,
            isUsable: d => app.ModelCatalog.Store.Get(d.Id)?.Status == ModelStatus.Ready);

        Console.WriteLine($"档位决策 : {decision.Pack.Id}{(decision.IsFallback ? "（降档）" : "")}");
        if (decision.Reason is { Length: > 0 } reason) Console.WriteLine($"           {reason}");

        Console.WriteLine();

        foreach (var engineId in AsrEngineCatalog.EngineIds)
        {
            if (engineId == AsrEngineCatalog.ChatAudio)
            {
                Console.WriteLine($"引擎 {engineId,-14} : 需要 --base-url/--model（回环端点），跳过探测");
                continue;
            }

            if (engineId == AsrEngineCatalog.DeepSeekTools)
            {
                Console.WriteLine($"引擎 {engineId,-14} : 需要 API Key（SDEMO_ASR_API_KEY），跳过探测");
                continue;
            }

            using var provider = AsrEngineCatalog.Create(engineId, app.ModelCatalog);
            var a = provider.Probe();
            Console.WriteLine($"引擎 {engineId,-14} : {(a.Available ? "可用" : "不可用")} {a.Reason} {a.Detail}");
            Console.WriteLine($"     能力        : 时间戳={provider.Capabilities.Timestamps} 端点={provider.Capabilities.Endpoint} anchor={provider.Capabilities.Anchor}");
        }

        Console.WriteLine();
        Console.WriteLine($"MCP 工具 : {string.Join(", ", MeetingToolCatalog.Tools.Select(t => t.Name))}");
        return 0;
    }

    private static int Tools()
    {
        var array = new System.Text.Json.Nodes.JsonArray();
        foreach (var tool in MeetingToolCatalog.Tools) array.Add(tool.ToJson());
        Console.WriteLine(array.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    private static int Audit(MeetingHost app, Options o)
    {
        var meetingId = o.Str("meeting");
        var records = new MeetingAuditLog(app.Session).List(meetingId, o.Int("limit", 50));

        if (records.Count == 0)
        {
            Console.WriteLine(meetingId is null ? "还没有审计记录" : $"{meetingId} 还没有审计记录");
            return 0;
        }

        foreach (var r in records)
        {
            var mark = r.Outcome == "ok" ? "✓" : "✗";
            Console.WriteLine(
                $"{r.At.ToLocalTime():MM-dd HH:mm:ss} {mark} {r.Actor,-5} {r.Tool,-24} {r.Action,-7} " +
                $"{r.EntityType}/{r.EntityId?[..8] ?? "-"} {r.Error ?? ""}");
        }

        return 0;
    }

    // ── serve ───────────────────────────────────────────────────────────

    /// <summary>起回环 HTTP：/mcp 给 DesktopAgent 用，/v1/health 给宿主探活。</summary>
    private static async Task<int> ServeAsync(MeetingHost app, Options o)
    {
        var token = o.Str("token")
                    ?? Environment.GetEnvironmentVariable("SDEMO_APP_TOKEN")
                    ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

        var mcp = new MeetingMcpServer(
            new MeetingQuery(app.Session),
            app.Meetings,
            new MeetingDomainService(app.Session));
        using var server = new McpHttpServer(
            token,
            MeetingConstants.AppId,
            "会议",
            "0.1.0",
            mcp,
            writeRuntimeFile: !o.Flag("no-runtime-file"));

        server.Start(o.Int("port", 0));

        using var done = new ManualResetEventSlim();
        server.ShutdownRequested = done.Set;

        Console.WriteLine($"sdemo.meeting MCP 已启动：{server.BaseUrl}/mcp");
        Console.WriteLine($"  应用配置：{McpHttpServer.RuntimePathOf(MeetingConstants.AppId)}");
        if (o.Flag("show-token")) Console.WriteLine($"  token   ：{token}");
        Console.WriteLine("  工具    ：" + string.Join(", ", MeetingToolCatalog.Tools.Select(t => t.Name)));
        Console.WriteLine("Ctrl+C 退出（或 POST /v1/shutdown）");

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            done.Set();
        };

        await Task.Run(done.Wait);
        server.Stop();
        return 0;
    }

    private static MeetingKind ParseKind(string? raw) =>
        Enum.TryParse<MeetingKind>(raw, ignoreCase: true, out var kind) ? kind : MeetingKind.AdHoc;

    private static int Unknown(string cmd)
    {
        Console.Error.WriteLine($"未知命令：{cmd}（试 --help）");
        return 1;
    }

    private static void Help() => Console.WriteLine("""
        meeting —— sdemo.meeting CLI（录 → 转 → 存）

        用法：
          meeting record      --title "周会" [--seconds 30] [--no-loopback] [--kind weekly]
          meeting import      --audio <wav> [--title "..."] [--transcribe]
          meeting transcribe  --meeting <id> [--engine whisper-cpp|chat-audio|deepseek-tools] [--lang zh] [--audio <wav>]
                              分段：--hotwords "SDemo,Qwen3" --max-segment 60000 --no-vad
                              云端 chat ：--base-url https://... --model <id> --allow-remote [--api-key <key>]
                              云端 tools：--engine deepseek-tools --allow-remote [--api-key <key>]
                                          （Key 也可放 SDEMO_ASR_API_KEY；≤120 秒 / ≤8 MB）
          meeting resume      --run <runId> [--audio <wav>] [--lang zh] [--engine ...]
          meeting list        [--limit 20]
          meeting show        <id>
          meeting models      [--import <文件> [--id <packId>] [--overwrite]] [--verify <id>] [--remove <id>]
          meeting audit       [--meeting <id>] [--limit 50]
          meeting serve       [--port 0] [--token <hex>] [--show-token] [--no-runtime-file]
          meeting doctor
          meeting tools

        环境变量：
          SDEMO_MEETING_DATA_DIR   数据根目录（默认 %APPDATA%\SDemo\Apps\sdemo.meeting）
          SDEMO_MEETING_MODEL_DIR  模型目录（默认 <数据根>\models）

        模型：把 ggml-large-v3-turbo-q8_0.bin 放进模型目录即可（874 MB）。
        """);

    // ── 组合根 ──────────────────────────────────────────────────────────

    // ── 参数 ────────────────────────────────────────────────────────────

    private sealed class Options
    {
        private readonly Dictionary<string, string?> _values = new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyList<string> Positional { get; private init; } = Array.Empty<string>();

        public static Options Parse(string[] args)
        {
            var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            var positional = new List<string>();

            for (var i = 1; i < args.Length; i++)
            {
                var a = args[i];
                if (!a.StartsWith("--", StringComparison.Ordinal))
                {
                    positional.Add(a);
                    continue;
                }

                var key = a[2..];
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    values[key] = args[++i];
                else
                    values[key] = "true";
            }

            return new Options { Positional = positional }.With(values);
        }

        private Options With(Dictionary<string, string?> values)
        {
            foreach (var kv in values) _values[kv.Key] = kv.Value;
            return this;
        }

        public string? Str(string key, string? fallback = null) =>
            _values.TryGetValue(key, out var v) && v is not null ? v : fallback;

        public void Set(string key, string? value) => _values[key] = value;

        public int Int(string key, int fallback) =>
            int.TryParse(Str(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

        public bool Flag(string key) =>
            _values.TryGetValue(key, out var v) && !string.Equals(v, "false", StringComparison.OrdinalIgnoreCase);
    }
}

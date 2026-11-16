using System.Globalization;
using System.Text;
using SDemo.App.Meeting.Asr;
using SDemo.App.Meeting.Audio;
using SDemo.App.Meeting.Contracts;
using SDemo.App.Meeting.Core;
using SDemo.App.Meeting.Mcp;
using SDemo.App.Meeting.Storage;

namespace SDemo.App.Meeting.Host;

/// <summary>
/// 阶段 A 的组合根：CLI 跑通「录 → 转 → 存」。
/// 界面（WPF）与 MCP server 是后续阶段，这里只保证数据面正确。
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
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
            using var app = MeetingApp.Open();

            return cmd switch
            {
                "record" => await RecordAsync(app, o),
                "import" => await ImportAsync(app, o),
                "transcribe" => await TranscribeAsync(app, o),
                "list" => List(app, o),
                "show" => Show(app, o),
                "models" => Models(app),
                "doctor" => Doctor(app),
                "tools" => Tools(),
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

    private static async Task<int> RecordAsync(MeetingApp app, Options o)
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
    private static async Task<int> ImportAsync(MeetingApp app, Options o)
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

    private static async Task<int> TranscribeAsync(MeetingApp app, Options o)
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

        using var provider = AsrEngineCatalog.Create(
            engineId,
            app.ModelCatalog,
            o.Str("base-url"),
            o.Str("model"),
            o.Str("whisper-model"),
            o.Str("api-key") ?? Environment.GetEnvironmentVariable(AsrEngineCatalog.ApiKeyEnvVar),
            o.Flag("allow-remote"));

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

        var runner = new TranscriptionRunner(app.Meetings);
        var slice = await runner.RunAsync(
            meeting.Id, audioPath, provider, language,
            progress: progress,
            ct: CancellationToken.None);

        Console.WriteLine();
        Console.WriteLine($"转写完成：{slice.Run.Engine} · {slice.Segments.Count} 段 · {slice.TotalChars} 字");
        foreach (var seg in slice.Segments)
            Console.WriteLine($"  [{seg.FormatTimecode()}] {seg.Text}");

        return 0;
    }

    // ── 只读命令 ────────────────────────────────────────────────────────

    private static int List(MeetingApp app, Options o)
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

    private static int Show(MeetingApp app, Options o)
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
                Console.WriteLine($"  [{seg.FormatTimecode()}] {seg.Text}");
        }

        return 0;
    }

    private static int Models(MeetingApp app)
    {
        Console.WriteLine($"模型目录：{app.ModelCatalog.ModelDir}");
        foreach (var pack in app.ModelCatalog.WhisperPacks())
        {
            Console.WriteLine($"  {pack.Id,-34} {pack.Bytes / 1024d / 1024d,8:F1} MB  {(pack.Bytes > 0 ? "已就绪" : "未下载")}  {(pack.Optional ? "可选" : "默认")}");
        }

        return 0;
    }

    private static int Doctor(MeetingApp app)
    {
        Console.WriteLine($"数据目录 : {MeetingConstants.ResolveDataDir()}");
        Console.WriteLine($"数据库   : {app.Database.Path}");
        Console.WriteLine($"录音设备 : {(DualTrackRecorder.HasCaptureDevice() ? "有" : "无")}");
        Console.WriteLine();

        foreach (var engineId in AsrEngineCatalog.EngineIds)
        {
            if (engineId == AsrEngineCatalog.ChatAudio)
            {
                Console.WriteLine($"引擎 {engineId,-14} : 需要 --base-url/--model（回环端点），跳过探测");
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
        Console.WriteLine(MeetingToolCatalog.ToJsonArray());
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
        meeting —— sdemo.meeting 阶段 A CLI（录 → 转 → 存）

        用法：
          meeting record      --title "周会" [--seconds 30] [--no-loopback] [--kind weekly]
          meeting import      --audio <wav> [--title "..."] [--transcribe]
          meeting transcribe  --meeting <id> [--engine whisper-cpp|chat-audio|deepseek-tools] [--lang zh] [--audio <wav>]
                              云端 chat ：--base-url https://... --model <id> --allow-remote [--api-key <key>]
                              云端 tools：--engine deepseek-tools --allow-remote [--api-key <key>]
                                          （Key 也可放 SDEMO_ASR_API_KEY；≤120 秒 / ≤8 MB，需先切段）
          meeting list        [--limit 20]
          meeting show        <id>
          meeting models
          meeting doctor
          meeting tools

        环境变量：
          SDEMO_MEETING_DATA_DIR   数据根目录（默认 %APPDATA%\SDemo\Apps\sdemo.meeting）
          SDEMO_MEETING_MODEL_DIR  模型目录（默认 <数据根>\models）

        模型：把 ggml-large-v3-turbo-q8_0.bin 放进模型目录即可（874 MB）。
        """);

    // ── 组合根 ──────────────────────────────────────────────────────────

    private sealed class MeetingApp : IDisposable
    {
        private MeetingApp(MeetingDatabase db, MeetingSession session, MeetingService meetings, MeetingModelCatalog models)
        {
            Database = db;
            Session = session;
            Meetings = meetings;
            ModelCatalog = models;
        }

        public MeetingDatabase Database { get; }
        public MeetingSession Session { get; }
        public MeetingService Meetings { get; }
        public MeetingModelCatalog ModelCatalog { get; }

        public static MeetingApp Open()
        {
            var dataDir = MeetingConstants.ResolveDataDir();
            Directory.CreateDirectory(dataDir);

            var db = MeetingDatabase.Open(Path.Combine(dataDir, MeetingConstants.DbFileName));
            var session = new MeetingSession(db);
            var meetings = new MeetingService(session);

            // 上次崩溃留下的 Running 标成 Interrupted（可续转）
            var recovered = meetings.RecoverInterruptedRuns();
            if (recovered > 0) Console.Error.WriteLine($"[info] {recovered} 个未完成的转写 run 已标记为 interrupted");

            return new MeetingApp(db, session, meetings, new MeetingModelCatalog());
        }

        public void Dispose() => Database.Dispose();
    }

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

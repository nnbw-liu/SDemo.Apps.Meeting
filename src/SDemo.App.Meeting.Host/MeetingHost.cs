using System.IO;
using Microsoft.Extensions.DependencyInjection;
using SDemo.App.Meeting.Asr;
using SDemo.App.Meeting.Contracts;
using SDemo.App.Meeting.Core;
using SDemo.App.Meeting.Mcp;
using SDemo.App.Meeting.Storage;

namespace SDemo.App.Meeting.Host;

/// <summary>
/// 组合根：数据库 + 服务 + 进程内 MCP。GUI 与 <c>--headless</c> 共用同一份，
/// 避免"界面一个库、CLI 另一个库"。
/// </summary>
internal sealed class MeetingHost : IDisposable
{
    private MeetingHost(MeetingDatabase database, MeetingSession session, MeetingService meetings, MeetingModelCatalog models)
    {
        Database = database;
        Session = session;
        Meetings = meetings;
        ModelCatalog = models;
    }

    public MeetingDatabase Database { get; }

    public MeetingSession Session { get; }

    public MeetingService Meetings { get; }

    public MeetingModelCatalog ModelCatalog { get; }

    public MeetingQuery Query => _query ??= new MeetingQuery(Session);
    private MeetingQuery? _query;

    public MeetingDomainService Domain => _domain ??= new MeetingDomainService(Session);
    private MeetingDomainService? _domain;

    public MeetingAuditLog Audit => _audit ??= new MeetingAuditLog(Session);
    private MeetingAuditLog? _audit;

    public MeetingMcpServer Mcp => _mcp ??= new MeetingMcpServer(Query, Meetings, Domain, Audit);
    private MeetingMcpServer? _mcp;

    public IRecorderController Recorder => _recorder ??= new RecordingCoordinator(
        Meetings,
        Session,
        new DualTrackCaptureFactory(Path.Combine(MeetingConstants.ResolveDataDir(), MeetingConstants.RecordingsDirName)),
        CreateWhisperProvider,
        Path.Combine(MeetingConstants.ResolveDataDir(), MeetingConstants.RecordingsDirName));

    private IRecorderController? _recorder;

    /// <summary>界面录音用的默认引擎：whisper（带内存降档）。模型没就绪时给一句能照做的话。</summary>
    private IAsrProvider CreateWhisperProvider()
    {
        var store = ModelCatalog.Store;
        if (!store.List().Any(m => m.Status == ModelStatus.Ready))
        {
            throw new MeetingException(MeetingErrorCodes.AsrUnavailable,
                "还没有可用的转写模型。先执行 `meeting models --import <模型文件>`，或安装自带模型的完整包。");
        }

        return AsrEngineCatalog.CreateWhisperWithFallback(ModelCatalog);
    }

    public McpHttpServer? Server { get; private set; }

    public string? McpToken { get; private set; }

    /// <summary>打开数据目录；<paramref name="dataDir"/> 为空时走环境变量/默认路径。</summary>
    public static MeetingHost Open(string? dataDir = null)
    {
        if (!string.IsNullOrWhiteSpace(dataDir))
            Environment.SetEnvironmentVariable(MeetingConstants.DataDirEnvVar, dataDir);

        var root = MeetingConstants.ResolveDataDir();
        Directory.CreateDirectory(root);

        var database = MeetingDatabase.Open(Path.Combine(root, MeetingConstants.DbFileName));
        var session = new MeetingSession(database);
        var meetings = new MeetingService(session);

        // 上次崩溃留下的 Running 标成 Interrupted（可续转）
        var recovered = meetings.RecoverInterruptedRuns();
        if (recovered > 0)
            Console.Error.WriteLine($"[info] {recovered} 个未完成的转写 run 已标记为 interrupted");

        return new MeetingHost(database, session, meetings, new MeetingModelCatalog());
    }

    /// <summary>起进程内 MCP（回环 + token + runtime 文件）。返回实际端口。</summary>
    public int StartMcp(string? token = null, int port = 0, bool writeRuntimeFile = true)
    {
        if (Server is not null) return Server.Port;

        McpToken = string.IsNullOrWhiteSpace(token)
            ? Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).ToLowerInvariant()
            : token;

        Server = new McpHttpServer(McpToken, MeetingConstants.AppId, "会议", "0.1.0", Mcp, writeRuntimeFile);
        Server.Start(port);
        return Server.Port;
    }

    public AnalysisQueueService Analysis => _analysis ??= new AnalysisQueueService(Session);

    private AnalysisQueueService? _analysis;

    public KbProjectionService Projection => _projection ??= new KbProjectionService(
        Session,
        Domain,
        () => KbConnection.TryRead() is { } connection ? new HttpKbTransport(connection) : null);

    private KbProjectionService? _projection;

    /// <summary>
    /// 启动时补投一次没成功的投影（KB 可能上次没开）。只补次数不多的，避免每次开都撞同一个错误。
    /// </summary>
    public async Task RetryPendingProjectionsAsync(int maxAttempts = 5, CancellationToken ct = default)
    {
        if (KbConnection.TryRead() is null) return;

        foreach (var pending in Session.ListPendingProjections())
        {
            if (pending.Attempts >= maxAttempts) continue;
            try
            {
                await Projection.ProjectAsync(pending.MeetingId, pending.IncludeTranscript, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Program.Log($"补投投影失败：{pending.MeetingId} {ex.Message}");
            }
        }
    }

    /// <summary>给 BlazorWebView 用的容器：组件要的服务都在这里。</summary>
    public ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        services.AddWpfBlazorWebView();

        services.AddSingleton(Database);
        services.AddSingleton(Session);
        services.AddSingleton(Meetings);
        services.AddSingleton(ModelCatalog);
        services.AddSingleton(Query);
        services.AddSingleton(Domain);
        services.AddSingleton(Audit);
        services.AddSingleton(new TranscriptEditor(Session));
        services.AddSingleton(Projection);
        services.AddSingleton(Analysis);
        services.AddSingleton(Recorder);

        return services.BuildServiceProvider();
    }

    public void Dispose()
    {
        Server?.Dispose();
        Server = null;
        Database.Dispose();
    }
}

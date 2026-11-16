using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SDemo.App.Meeting.Contracts;
using SDemo.App.Meeting.Mcp;

namespace SDemo.App.Meeting.Host;

/// <summary>
/// 会议的回环 HTTP 面（hub-protocol §3/§6）：只监听 127.0.0.1，随机端口，
/// <c>/mcp</c> 走 Bearer token，<c>/v1/health</c> 免鉴权，启动时写应用配置、退出时改回 stopped。
///
/// 传输层只做「鉴权 + 路由 + 体积上限」，工具语义全在 <see cref="MeetingMcpServer"/>。
/// </summary>
internal sealed class McpHttpServer : IDisposable
{
    private const int MaxRequestBytes = 2 * 1024 * 1024;

    private readonly string _token;
    private readonly string _appId;
    private readonly string _displayName;
    private readonly string _version;
    private readonly MeetingMcpServer _mcp;
    private readonly DateTimeOffset _startedAt;
    private readonly bool _writeRuntimeFile;

    private HttpListener? _listener;
    private Task? _loop;
    private int _stopped;

    public McpHttpServer(
        string token,
        string appId,
        string displayName,
        string version,
        MeetingMcpServer mcp,
        bool writeRuntimeFile = true)
    {
        _token = token;
        _appId = appId;
        _displayName = displayName;
        _version = version;
        _mcp = mcp;
        _writeRuntimeFile = writeRuntimeFile;
        _startedAt = DateTimeOffset.Now;
    }

    public int Port { get; private set; }

    public string BaseUrl => $"http://127.0.0.1:{Port}";

    /// <summary>收到 /v1/shutdown 后回调；由组合根负责优雅退出。</summary>
    public Action? ShutdownRequested { get; set; }

    public static string RuntimeDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SDemo", "Hub", "apps");

    public static string RuntimePathOf(string appId) => Path.Combine(RuntimeDirectory, appId + ".json");

    public void Start(int port = 0)
    {
        var listener = new HttpListener();

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var candidate = port != 0 ? port : FindFreePort();
            listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{candidate}/");

            try
            {
                listener.Start();
                _listener = listener;
                Port = candidate;
                WriteRuntimeFile(running: true);
                _loop = Task.Run(LoopAsync);
                return;
            }
            catch (HttpListenerException) when (attempt < 4)
            {
                listener.Close();
                if (port != 0) throw;
            }
        }

        throw new InvalidOperationException("无法绑定本机回环端口");
    }

    public void Stop()
    {
        if (Interlocked.Exchange(ref _stopped, 1) == 1) return;

        try { _listener?.Stop(); } catch (ObjectDisposedException) { }
        try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        _listener?.Close();
        _listener = null;

        WriteRuntimeFile(running: false);
    }

    private async Task LoopAsync()
    {
        var listener = _listener!;
        while (listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (HttpListenerException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            _ = Task.Run(() => HandleAsync(context));
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        try
        {
            var path = context.Request.Url?.AbsolutePath ?? "/";

            if (path == "/v1/health" && context.Request.HttpMethod == "GET")
            {
                await RespondAsync(context, 200, new JsonObject { ["status"] = "ok" }).ConfigureAwait(false);
                return;
            }

            if (!IsAuthorized(context.Request))
            {
                await RespondAsync(context, 401, new JsonObject { ["error"] = "unauthorized" }).ConfigureAwait(false);
                return;
            }

            if (path == "/mcp" && context.Request.HttpMethod == "POST")
            {
                await HandleMcpAsync(context).ConfigureAwait(false);
                return;
            }

            if (path == "/v1/info" && context.Request.HttpMethod == "GET")
            {
                await RespondAsync(context, 200, BuildInfo()).ConfigureAwait(false);
                return;
            }

            if (path == "/v1/shutdown" && context.Request.HttpMethod == "POST")
            {
                await RespondAsync(context, 200, new JsonObject { ["status"] = "stopping" }).ConfigureAwait(false);
                // 让响应先发出去，再回调退出
                _ = Task.Run(() => ShutdownRequested?.Invoke());
                return;
            }

            await RespondAsync(context, 404, new JsonObject { ["error"] = "not found" }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            try
            {
                await RespondAsync(context, 500, new JsonObject { ["error"] = ex.GetType().Name }).ConfigureAwait(false);
            }
            catch
            {
                // 连接已断，忽略
            }
        }
        finally
        {
            try { context.Response.Close(); } catch { }
        }
    }

    private async Task HandleMcpAsync(HttpListenerContext context)
    {
        if (context.Request.ContentLength64 > MaxRequestBytes)
        {
            await RespondAsync(context, 413, new JsonObject { ["error"] = "payload too large" }).ConfigureAwait(false);
            return;
        }

        using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
        var body = await reader.ReadToEndAsync().ConfigureAwait(false);

        var response = _mcp.HandleJson(body);

        if (string.IsNullOrEmpty(response))
        {
            context.Response.StatusCode = 202;
            return;
        }

        await RespondAsync(context, 200, raw: response).ConfigureAwait(false);
    }

    private bool IsAuthorized(HttpListenerRequest request)
    {
        var header = request.Headers["Authorization"];
        if (string.IsNullOrWhiteSpace(header)) return false;

        const string prefix = "Bearer ";
        if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;

        var presented = header[prefix.Length..].Trim();
        return CryptographicEquals(presented, _token);
    }

    private static bool CryptographicEquals(string a, string b)
    {
        if (a.Length != b.Length) return false;
        var diff = 0;
        for (var i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
        return diff == 0;
    }

    private JsonObject BuildInfo() => new()
    {
        ["schemaVersion"] = 1,
        ["appId"] = _appId,
        ["displayName"] = _displayName,
        ["version"] = _version,
        ["kind"] = "app",
        ["mcp"] = new JsonObject { ["transport"] = "http", ["path"] = "/mcp" },
        ["tools"] = new JsonArray(MeetingToolCatalog.Tools
            .Select(t => (JsonNode)new JsonObject { ["name"] = t.Name, ["risk"] = t.Risk.ToString().ToLowerInvariant() })
            .ToArray()),
    };

    private void WriteRuntimeFile(bool running)
    {
        if (!_writeRuntimeFile) return;

        try
        {
            Directory.CreateDirectory(RuntimeDirectory);
            var target = RuntimePathOf(_appId);

            var payload = running
                ? new JsonObject
                {
                    ["schemaVersion"] = 1,
                    ["appId"] = _appId,
                    ["displayName"] = _displayName,
                    ["version"] = _version,
                    ["kind"] = "app",
                    ["executable"] = Environment.ProcessPath,
                    ["arguments"] = new JsonArray(),
                    ["port"] = Port,
                    ["baseUrl"] = BaseUrl,
                    ["token"] = _token,
                    ["pid"] = Environment.ProcessId,
                    ["startedAt"] = _startedAt.ToString("O"),
                    ["state"] = "running",
                    ["mcp"] = new JsonObject { ["transport"] = "http", ["path"] = "/mcp" },
                    ["widgets"] = new JsonArray(),
                }
                : new JsonObject
                {
                    ["schemaVersion"] = 1,
                    ["appId"] = _appId,
                    ["displayName"] = _displayName,
                    ["version"] = _version,
                    ["kind"] = "app",
                    ["port"] = Port,
                    ["baseUrl"] = BaseUrl,
                    ["state"] = "stopped",
                    ["mcp"] = new JsonObject { ["transport"] = "http", ["path"] = "/mcp" },
                    ["widgets"] = new JsonArray(),
                };

            // 契约要求：同目录临时文件 + 覆盖移动，避免读到半截 JSON
            var temp = target + ".tmp";
            File.WriteAllText(temp, payload.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, target, overwrite: true);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[warn] 写运行期文件失败：{ex.Message}");
        }
    }

    private static async Task RespondAsync(HttpListenerContext context, int status, JsonObject? payload = null, string? raw = null)
    {
        var body = raw ?? payload?.ToJsonString() ?? "{}";
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
    }

    private static int FindFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public void Dispose() => Stop();
}

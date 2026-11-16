using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using SDemo.App.Meeting.Contracts;

namespace SDemo.App.Meeting.Core;

/// <summary>
/// KB 的连接信息（kb-design §3.2）：地址取 KB 自己写的连接配置，token 取 KB 给本应用签发的文件。
/// 两者缺一就认为 KB 没装/没启动——投影走"排队 + 重试"，不阻塞会议。
/// </summary>
public sealed record KbConnection(string BaseUrl, string Token)
{
    public static string AppsDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SDemo", "Hub", "apps");

    public static string TokenPathOf(string appId) =>
        Path.Combine(AppsDirectory, "sdemo.kb", "tokens", appId + ".token");

    public static string ConfigPathOf(string appId) => Path.Combine(AppsDirectory, "sdemo.kb.json");

    /// <summary>读不到（KB 未安装 / 未启动 / 没发 token）返回 null，调用方据此降级。</summary>
    public static KbConnection? TryRead(string appId = MeetingConstants.AppId)
    {
        try
        {
            var configPath = ConfigPathOf(appId);
            var tokenPath = TokenPathOf(appId);
            if (!File.Exists(configPath) || !File.Exists(tokenPath)) return null;

            var config = JsonNode.Parse(File.ReadAllText(configPath)) as JsonObject;

            // KB 的配置里没有 baseUrl/state，只有 ingestUrl/healthUrl 这类完整地址（见 Kb AGENTS §运行期文件）
            var baseUrl = config?["baseUrl"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                var probe = config?["ingestUrl"]?.GetValue<string>() ?? config?["healthUrl"]?.GetValue<string>();
                if (probe is not null && Uri.TryCreate(probe, UriKind.Absolute, out var uri))
                    baseUrl = uri.GetLeftPart(UriPartial.Authority);
            }

            var state = config?["state"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(baseUrl)) return null;
            if (state is not null && !state.Equals("running", StringComparison.OrdinalIgnoreCase)) return null;

            var token = File.ReadAllText(tokenPath).Trim();
            return string.IsNullOrWhiteSpace(token) ? null : new KbConnection(baseUrl.TrimEnd('/'), token);
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>KB 写入面的传输抽象。拆出来是为了让投影逻辑能在没有 KB 的机器上被测。</summary>
public interface IKbTransport
{
    Task<KbIngestResult> IngestAsync(string json, CancellationToken ct);

    /// <summary>升格/降格保留等级（归档不是新建，是同一 docId 改 retention）。</summary>
    Task<KbIngestResult> PromoteAsync(string json, CancellationToken ct);
}

public sealed class HttpKbTransport : IKbTransport, IDisposable
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;

    public HttpKbTransport(KbConnection connection, HttpClient? http = null)
    {
        _baseUrl = connection.BaseUrl;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", connection.Token);
    }

    public Task<KbIngestResult> IngestAsync(string json, CancellationToken ct) =>
        PostAsync("/v1/ingest", json, ct);

    public Task<KbIngestResult> PromoteAsync(string json, CancellationToken ct) =>
        PostAsync("/v1/promote", json, ct);

    private async Task<KbIngestResult> PostAsync(string path, string json, CancellationToken ct)
    {
        try
        {
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync(_baseUrl + path, content, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return new KbIngestResult(null, null, $"HTTP {(int)response.StatusCode}：{Trim(body)}");

            var node = JsonNode.Parse(body) as JsonObject;
            var docId = node?["docId"]?.GetValue<string>() ?? node?["id"]?.GetValue<string>();
            var version = node?["version"] is JsonValue v && v.TryGetValue<int>(out var n) ? n : (int?)null;

            return docId is null
                ? new KbIngestResult(null, version, $"KB 返回里没有 docId：{Trim(body)}")
                : new KbIngestResult(docId, version, null);
        }
        catch (Exception ex)
        {
            return new KbIngestResult(null, null, $"{ex.GetType().Name}：{ex.Message}");
        }
    }

    private static string Trim(string text) =>
        text.Length <= 300 ? text : text[..300] + "…";

    public void Dispose() => _http.Dispose();
}

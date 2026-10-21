using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using VTC.Shared.Dtos;

namespace VTC.Client.Services;

/// <summary>统一 API 异常（含业务码，p0-contract §1 错误响应）。</summary>
public sealed class ApiException : Exception
{
    public int Code { get; }
    public int HttpStatus { get; }
    public object? Details { get; }
    public ApiException(string msg, int code, int httpStatus, object? details) : base(msg)
    {
        Code = code; HttpStatus = httpStatus; Details = details;
    }
}

/// <summary>
/// 强类型 HTTP 客户端（System.Net.Http.Json，规划 §4.1）。
/// - 统一 /api/v1 前缀、错误响应 {code,message,details}；
/// - Bearer JWT 注入 + 401 时单次 refresh 后重试原请求；
/// - 分页端点由 DTO 自带（PageResult&lt;T&gt;）。
/// </summary>
public interface IApiClient
{
    HttpClient Http { get; }
    string ApiBase { get; }
    Task<T?> GetAsync<T>(string path, Dictionary<string, string?>? query = null, bool useAuth = true);
    Task<string> GetStringAsync(string path, Dictionary<string, string?>? query = null, bool useAuth = false);
    Task<T?> PostAsync<T>(string path, object? body = null, bool useAuth = true);
    Task<T?> PatchAsync<T>(string path, object? body = null, bool useAuth = true);
    Task<T?> DeleteAsync<T>(string path, bool useAuth = true);
    Task<HttpResponseMessage> PutStreamAsync(string url, Stream stream, long? contentLength, CancellationToken ct);
}

public sealed class ApiClient : IApiClient
{
    // 懒加载 AuthService：避免 DI 循环依赖
    //   BrandingService → IApiClient → IAuthService → IApiClient（环）。
    //   构造时不解析 IAuthService；真正需要 token 时（ApplyAuth / refresh）
    //   通过 AppServiceLocator 现取（此时容器已构建完成）。
    private readonly ILogger<ApiClient> _log;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private bool _refreshing;

    public HttpClient Http { get; }
    public string ApiBase { get; }

    private IAuthService? ResolveAuth()
    {
        try { return AppServiceLocator.Services?.GetService<IAuthService>(); }
        catch { return null; }
    }

    public ApiClient(IConfiguration cfg, ILogger<ApiClient> log)
    {
        _log = log;
        ApiBase = (EnvLoader.Get(cfg, "VTC_API_BASE") ?? "http://127.0.0.1:8731").TrimEnd('/');
        Http = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = 8,
        })
        {
            Timeout = TimeSpan.FromMinutes(5),
        };
    }

    private void ApplyAuth(HttpRequestMessage req)
    {
        var token = ResolveAuth()?.AccessToken;
        if (!string.IsNullOrWhiteSpace(token))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private Uri BuildUri(string path, Dictionary<string, string?>? query = null)
    {
        if (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return new Uri(path);

        var builder = new UriBuilder(ApiBase);
        builder.Path = path.StartsWith('/') ? path.TrimStart('/') : path;
        if (query is { Count: > 0 })
        {
            var qs = string.Join('&', query
                .Where(kv => kv.Value != null)
                .Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value!)}"));
            builder.Query = qs;
        }
        return builder.Uri;
    }

    internal static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>GET 强类型（DTO / 标量）。</summary>
    public async Task<T?> GetAsync<T>(string path, Dictionary<string, string?>? query = null, bool useAuth = true)
    {
        return await SendWithRetryAsync<T>(async ct =>
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, BuildUri(path, query));
            if (useAuth) ApplyAuth(req);
            // resp 不在此 dispose：生命周期交给 SendWithRetryAsync（它内部 using 管理），
            // 否则 return 后 using 立即释放 HttpResponseMessage，外层再读 Content 会抛
            // "Cannot access a disposed object: HttpConnectionResponseContent"。
            var resp = await Http.SendAsync(req, ct);
            return resp;
        }, useAuth);
    }

    /// <summary>GET 原始文本（非 JSON 响应，如 updates 检查）。</summary>
    public async Task<string> GetStringAsync(string path, Dictionary<string, string?>? query = null, bool useAuth = false)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using var req = new HttpRequestMessage(HttpMethod.Get, BuildUri(path, query));
        if (useAuth) ApplyAuth(req);
        using var resp = await Http.SendAsync(req, cts.Token);
        await EnsureOkAsync(resp);
        return await resp.Content.ReadAsStringAsync(cts.Token);
    }

    /// <summary>POST（JSON body → JSON 响应）。</summary>
    public async Task<T?> PostAsync<T>(string path, object? body = null, bool useAuth = true)
    {
        return await SendWithRetryAsync<T>(async ct =>
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, BuildUri(path));
            if (body != null) req.Content = JsonContent.Create(body, options: JsonOpts);
            if (useAuth) ApplyAuth(req);
            var resp = await Http.SendAsync(req, ct);
            return resp;
        }, useAuth);
    }

    /// <summary>PATCH（JSON body → JSON 响应）。</summary>
    public async Task<T?> PatchAsync<T>(string path, object? body = null, bool useAuth = true)
    {
        return await SendWithRetryAsync<T>(async ct =>
        {
            using var req = new HttpRequestMessage(new HttpMethod("PATCH"), BuildUri(path));
            if (body != null) req.Content = JsonContent.Create(body, options: JsonOpts);
            if (useAuth) ApplyAuth(req);
            var resp = await Http.SendAsync(req, ct);
            return resp;
        }, useAuth);
    }

    /// <summary>DELETE。</summary>
    public async Task<T?> DeleteAsync<T>(string path, bool useAuth = true)
    {
        return await SendWithRetryAsync<T>(async ct =>
        {
            using var req = new HttpRequestMessage(HttpMethod.Delete, BuildUri(path));
            if (useAuth) ApplyAuth(req);
            var resp = await Http.SendAsync(req, ct);
            return resp;
        }, useAuth);
    }

    /// <summary>PUT 原始流（预签名直传上传，不走 API 鉴权）。</summary>
    public async Task<HttpResponseMessage> PutStreamAsync(string url, Stream stream, long? contentLength, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Put, new Uri(url));
        req.Content = new StreamContent(stream);
        if (contentLength.HasValue) req.Content.Headers.ContentLength = contentLength.Value;
        return await Http.SendAsync(req, ct);
    }

    /// <summary>带 401→refresh→重试 一次的发送核心。</summary>
    private async Task<T?> SendWithRetryAsync<T>(
        Func<CancellationToken, Task<HttpResponseMessage>> send, bool useAuth)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        using var resp = await send(cts.Token);
        var text = await resp.Content.ReadAsStringAsync(cts.Token);

        if (resp.StatusCode == HttpStatusCode.Unauthorized && useAuth && ResolveAuth() != null)
        {
            var newToken = await TryRefreshCoreAsync();
            if (newToken != null)
            {
                _log.LogInformation("401 → token 已刷新，重试原请求一次");
                resp.Dispose();
                using var resp2 = await send(cts.Token);
                var text2 = await resp2.Content.ReadAsStringAsync(cts.Token);
                return await FinalizeAsync<T>(resp2, text2);
            }
        }

        return await FinalizeAsync<T>(resp, text);
    }

    private async Task<T?> FinalizeAsync<T>(HttpResponseMessage resp, string text)
    {
        if (!resp.IsSuccessStatusCode)
        {
            var (code, msg, details) = ParseError(text, (int)resp.StatusCode);
            throw new ApiException(msg, code, (int)resp.StatusCode, details);
        }
        if (string.IsNullOrWhiteSpace(text)) return default;
        try
        {
            return JsonSerializer.Deserialize<T>(text, JsonOpts);
        }
        catch (JsonException ex)
        {
            throw new ApiException($"响应解析失败: {ex.Message}", 500, (int)resp.StatusCode,
                text.Length < 500 ? text : null);
        }
    }

    private async Task EnsureOkAsync(HttpResponseMessage resp)
    {
        if (resp.IsSuccessStatusCode) return;
        var text = await resp.Content.ReadAsStringAsync();
        var (code, msg, details) = ParseError(text, (int)resp.StatusCode);
        throw new ApiException(msg, code, (int)resp.StatusCode, details);
    }

    private static (int code, string msg, object? details) ParseError(string text, int httpStatus)
    {
        try
        {
            var err = JsonSerializer.Deserialize<ApiError>(text, JsonOpts);
            if (err != null && (err.Code != 0 || !string.IsNullOrWhiteSpace(err.Message)))
                return (err.Code == 0 ? httpStatus : err.Code, err.Message, err.Details);
        }
        catch { /* fallthrough */ }
        return (httpStatus, $"HTTP {httpStatus}", null);
    }

    private async Task<string?> TryRefreshCoreAsync()
    {
        var auth = ResolveAuth();
        if (_refreshing || auth?.RefreshToken == null) return null;
        await _refreshLock.WaitAsync();
        try
        {
            if (_refreshing) return null;
            _refreshing = true;
            return auth.TryRefresh();
        }
        finally
        {
            _refreshLock.Release();
        }
    }
}

/// <summary>认证服务抽象（解耦 ApiClient 与 AuthService 循环依赖）。</summary>
public interface IAuthService
{
    string? AccessToken { get; }
    string? RefreshToken { get; }
    bool IsAuthenticated { get; }
    /// <summary>access token 过期时尝试刷新；成功返回新 access token，失败返回 null。</summary>
    string? TryRefresh();
    event EventHandler? AuthChanged;
}

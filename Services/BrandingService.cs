using System.IO;
using System.Net;
using Avalonia.Media.Imaging;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using VTC.Shared;
using VTC.Shared.Dtos;

namespace VTC.Client.Services;

/// <summary>
/// 品牌/换肤（C1，规划 §2.3）。
/// - 启动拉 GET /api/v1/config/public（AuthApiClient 已封装）→ 覆盖 .env 品牌字段；
/// - 本地缓存（离线兜底）；
/// - 远程图安全规则：域名白名单（API 同源 + 白名单 CDN）、格式/尺寸/体积校验、
///   ETag/Last-Modified 缓存、失败回退 pack:// 兜底；
/// - 远程图仅用于品牌展示，不参与鉴权/存储。
/// </summary>
public sealed class BrandingService
{
    private readonly IApiClient _api;
    private readonly IConfiguration _cfg;
    private readonly ILogger<BrandingService> _log;
    private readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions());
    private readonly object _gate = new();

    public const string CacheDirName = ".brand-cache";
    private const int MaxEdgePx = 4096;
    private const long MaxBytes = 10 * 1024 * 1024;
    private static readonly string[] AllowedFormats = ["jpg", "jpeg", "png", "webp"];

    public PublicConfigDto Current { get; private set; } = new();
    /// <summary>暴露配置（App 读 VTC_APP_ICON / VTC_CDN_DOMAINS 用）。</summary>
    public IConfiguration Cfg => _cfg;
    public event EventHandler? BrandChanged;

    public BrandingService(IApiClient api, IConfiguration cfg, ILogger<BrandingService> log)
    {
        _api = api;
        _cfg = cfg;
        _log = log;
    }

    /// <summary>启动第一调用：拉取 /config/public 并合并到 .env 品牌字段。失败则用 .env 兜底。</summary>
    public async Task<PublicConfigDto> LoadAsync(bool forceRemote = false)
    {
        var cfg = new PublicConfigDto
        {
            TeamName = ServicesEnv(_cfg, "TEAM_NAME") ?? "VTC 车队",
            TeamSlogan = ServicesEnv(_cfg, "TEAM_SLOGAN") ?? "",
            TeamLogo = ServicesEnv(_cfg, "TEAM_LOGO") ?? "",
            LoginBg = ServicesEnv(_cfg, "LOGIN_BG") ?? "",
            LoginLayout = (ServicesEnv(_cfg, "LOGIN_LAYOUT") ?? "left").ToLowerInvariant(),
            AccentColor = ServicesEnv(_cfg, "ACCENT_COLOR") ?? "#1E88E5",
            DefaultLang = "zh-CN",
            UpdateChannel = ServicesEnv(_cfg, "UPDATE_CHANNEL") ?? "stable",
            Features = new FeaturesDto
            {
                AllowUpload = (ServicesEnv(_cfg, "ALLOW_UPLOAD") ?? "true").Equals("true", StringComparison.OrdinalIgnoreCase),
            },
        };

        if (forceRemote)
            _cache.Remove("public_config");

        var cached = _cache.TryGetValue("public_config", out PublicConfigDto? c) ? c : null;
        try
        {
            var remote = await _api.GetAsync<PublicConfigDto>(VtcApi.ConfigPublic);
            if (remote != null)
            {
                // 后端覆盖 .env 品牌字段（仅非空字段覆盖，允许 .env 单独定制）
                MergeBrand(cfg, remote);
                _cache.Set("public_config", remote, TimeSpan.FromHours(1));
            }
        }
        catch (Exception ex)
        {
            _log.LogInformation("拉取 /config/public 失败（使用 .env 兜底）: {Msg}", ex.Message);
            if (cached != null)
            {
                _log.LogInformation("使用本地缓存的品牌配置");
                MergeBrand(cfg, cached);
            }
        }

        Current = cfg;
        BrandChanged?.Invoke(this, EventArgs.Empty);
        return cfg;
    }

    private static void MergeBrand(PublicConfigDto baseCfg, PublicConfigDto remote)
    {
        if (!string.IsNullOrWhiteSpace(remote.TeamName)) baseCfg.TeamName = remote.TeamName;
        if (!string.IsNullOrWhiteSpace(remote.TeamSlogan)) baseCfg.TeamSlogan = remote.TeamSlogan;
        if (!string.IsNullOrWhiteSpace(remote.TeamLogo)) baseCfg.TeamLogo = remote.TeamLogo;
        if (!string.IsNullOrWhiteSpace(remote.LoginBg)) baseCfg.LoginBg = remote.LoginBg;
        if (!string.IsNullOrWhiteSpace(remote.LoginLayout)) baseCfg.LoginLayout = remote.LoginLayout.ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(remote.AccentColor)) baseCfg.AccentColor = remote.AccentColor;
        if (!string.IsNullOrWhiteSpace(remote.DefaultLang)) baseCfg.DefaultLang = remote.DefaultLang;
        if (!string.IsNullOrWhiteSpace(remote.UpdateChannel)) baseCfg.UpdateChannel = remote.UpdateChannel;
        baseCfg.Features = remote.Features ?? baseCfg.Features;
    }

    private static string? ServicesEnv(IConfiguration cfg, string key) => EnvLoader.Get(cfg, key);

    #region 远程品牌图（白名单 + 校验 + 缓存 + 回退）

    /// <summary>
    /// 解析品牌图为 Bitmap（供 Image.Source）。
    /// 规则（§2.3）：pack:// 直接加载；https 必须过域名白名单；
    /// 格式 jpg/png/webp、最大边 ≤4096px、体积 ≤10MB；ETag/Last-Modified 命中不重复下载；失败回退本地兜底。
    /// </summary>
    public async Task<Bitmap?> ResolveBrandImageAsync(string source, string fallbackPack, bool isLogo)
    {
        if (string.IsNullOrWhiteSpace(source))
            return await LoadPackAsync(fallbackPack);

        if (source.StartsWith("pack://", StringComparison.OrdinalIgnoreCase))
            return await LoadPackAsync(source) ?? await LoadPackAsync(fallbackPack);

        // 本地文件路径（如 D:\brand\icon.ico / C:/logo.png）：存在则直接加载。
        // 此前只认 pack:// 和 http(s)，本地文件被误判为"源不合法"回退兜底 —— 车队用本地图标/Logo 时失效。
        if (System.IO.File.Exists(source))
            return OpenValidatedFile(source) ?? await LoadPackAsync(fallbackPack);

        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "https" && uri.Scheme != "http"))
        {
            _log.LogWarning("品牌图源不合法（允许 pack:// / 本地文件 / http(s)）: {Src}", source);
            return await LoadPackAsync(fallbackPack);
        }

        if (!IsDomainAllowed(uri))
        {
            _log.LogWarning("品牌图域名不在白名单，回退本地兜底: {Host}", uri.Host);
            return await LoadPackAsync(fallbackPack);
        }

        var cacheFile = GetCacheFile(uri, isLogo);
        try
        {
            // 缓存命中：ETag/Last-Modified 校验（简化：文件存在且 <24h 直接复用）
            if (File.Exists(cacheFile) &&
                (DateTime.UtcNow - File.GetLastWriteTimeUtc(cacheFile)) < TimeSpan.FromHours(24))
            {
                return OpenValidatedFile(cacheFile);
            }

            using var http = HttpClientPool.Shared;
            var req = new HttpRequestMessage(HttpMethod.Get, uri);
            using var resp = await http.SendAsync(req);
            if (!resp.IsSuccessStatusCode)
                throw new WebException($"HTTP {(int)resp.StatusCode}");

            using var ms = new MemoryStream();
            await resp.Content.CopyToAsync(ms);
            ms.Position = 0;

            if (ms.Length > MaxBytes)
            {
                _log.LogWarning("品牌图体积 {Size}B 超限(10MB)，回退本地兜底: {Host}", ms.Length, uri.Host);
                return await LoadPackAsync(fallbackPack);
            }

            var fmt = GetImageFormat(ms);
            if (fmt == null)
            {
                _log.LogWarning("品牌图格式不受支持（仅 jpg/png/webp），回退本地兜底: {Host}", uri.Host);
                return await LoadPackAsync(fallbackPack);
            }

            var bmp = new Bitmap(new MemoryStream(ms.ToArray()));
            if (Math.Max(bmp.PixelSize.Width, bmp.PixelSize.Height) > MaxEdgePx)
            {
                _log.LogWarning("品牌图尺寸 {W}x{H} 超限(4096px)，回退本地兜底: {Host}", bmp.PixelSize.Width, bmp.PixelSize.Height, uri.Host);
                bmp.Dispose();
                return await LoadPackAsync(fallbackPack);
            }

            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(cacheFile)!);
            ms.Position = 0;
            await System.IO.File.WriteAllBytesAsync(cacheFile, ms.ToArray());
            return bmp;
        }
        catch (Exception ex)
        {
            _log.LogWarning("远程品牌图加载失败，回退本地兜底: {Ex}", ex.Message);
            if (File.Exists(cacheFile))
            {
                var b = OpenValidatedFile(cacheFile);
                if (b != null) return b;
            }
            return await LoadPackAsync(fallbackPack);
        }
    }

    private bool IsDomainAllowed(Uri uri)
    {
        var apiBase = EnvLoader.Get(_cfg, "VTC_API_BASE");
        if (apiBase != null && Uri.TryCreate(apiBase, UriKind.Absolute, out var apiUri) &&
            string.Equals(uri.Host, apiUri.Host, StringComparison.OrdinalIgnoreCase) &&
            uri.Port == apiUri.Port)
            return true;

        // 白名单 CDN 域名：.env 的 VTC_CDN_DOMAINS（逗号分隔）
        var cdn = EnvLoader.Get(_cfg, "VTC_CDN_DOMAINS");
        if (cdn != null)
        {
            foreach (var d in cdn.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (string.Equals(uri.Host, d.TrimStart('.'), StringComparison.OrdinalIgnoreCase) ||
                    uri.Host.EndsWith("." + d.TrimStart('.'), StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }

    private static string? GetImageFormat(Stream s)
    {
        var buf = new byte[12];
        s.Read(buf, 0, buf.Length);
        s.Position = 0;
        if (buf.Length >= 3 && buf[0] == 0xFF && buf[1] == 0xD8 && buf[2] == 0xFF) return "jpg";
        if (buf.Length >= 8 && buf[0] == 0x89 && buf[1] == 'P' && buf[2] == 'N' && buf[3] == 'G') return "png";
        if (buf.Length >= 12 && buf[0] == 'R' && buf[1] == 'I' && buf[2] == 'F' && buf[3] == 'F' &&
            buf[8] == 'W' && buf[9] == 'E' && buf[10] == 'B' && buf[11] == 'P') return "webp";
        return null;
    }

    private Bitmap? OpenValidatedFile(string file)
    {
        try
        {
            var fmt = GetImageFormat(File.OpenRead(file));
            if (fmt == null) return null;
            return new Bitmap(file);
        }
        catch
        {
            return null;
        }
    }

    private string GetCacheFile(Uri uri, bool isLogo)
    {
        var dir = Path.Combine(Paths.CacheRoot, CacheDirName, isLogo ? "logo" : "login_bg");
        var safeHost = uri.Host.Replace(':', '_');
        var pathPart = uri.AbsolutePath.Trim('/').Replace('/', '_').Replace('\\', '_');
        var ext = Path.GetExtension(uri.AbsolutePath);
        if (string.IsNullOrEmpty(ext)) ext = ".jpg";
        return Path.Combine(dir, $"{safeHost}_{pathPart}{ext}");
    }

    private Task<Bitmap?> LoadPackAsync(string packUri)
    {
        try
        {
            var bmp = new Bitmap(packUri);
            return Task.FromResult<Bitmap?>(bmp);
        }
        catch (Exception ex)
        {
            _log.LogWarning("加载 pack:// 品牌图失败 {Uri}: {Ex}", packUri, ex.Message);
            return Task.FromResult<Bitmap?>(null);
        }
    }

    #endregion
}

internal static class HttpClientPool
{
    public static readonly HttpClient Shared = BuildClient();

    private static HttpClient BuildClient()
    {
        var h = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            AllowAutoRedirect = true,
            MaxConnectionsPerServer = 8,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        })
        {
            Timeout = TimeSpan.FromMinutes(2),
        };
        return h;
    }
}

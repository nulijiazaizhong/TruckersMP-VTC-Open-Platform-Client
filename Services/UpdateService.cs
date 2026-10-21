using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace VTC.Client.Services;

/// <summary>
/// 客户端自更新（C12）。
/// 检查 VTC_API_BASE/updates（或 GitHub Release，P0 优先 API）比对渠道版本 →
/// 下载 → 校验 → 提示重启（实际替换由外部启动器/用户执行，P0 不自动替换运行中程序）。
/// </summary>
public sealed class UpdateService
{
    private readonly IApiClient _api;
    private readonly IConfiguration _cfg;
    private readonly ILogger<UpdateService> _log;
    private readonly SaveDownloader _downloader;

    public string CurrentVersion { get; }

    public UpdateService(IApiClient api, IConfiguration cfg, ILogger<UpdateService> log, SaveDownloader downloader)
    {
        _api = api; _cfg = cfg; _log = log; _downloader = downloader;
        CurrentVersion = typeof(UpdateService).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }

    public sealed class UpdateInfo
    {
        public bool UpdateAvailable { get; init; }
        public string? LatestVersion { get; init; }
        public string? Notes { get; init; }
        public string? DownloadUrl { get; init; }
        public string? Sha256 { get; init; }
        public long Size { get; init; }
    }

    public async Task<UpdateInfo> CheckAsync(string? channel = null)
    {
        channel ??= EnvLoader.Get(_cfg, "UPDATE_CHANNEL") ?? "stable";
        try
        {
            var dto = await _api.GetAsync<UpdateCheckDto>("/updates",
                new Dictionary<string, string?> { ["channel"] = channel, ["current"] = CurrentVersion });
            if (dto == null)
                return new UpdateInfo { UpdateAvailable = false, LatestVersion = CurrentVersion };

            bool isNewer = Version.TryParse(dto.Latest, out var lv) &&
                           Version.TryParse(CurrentVersion, out var cv) && lv > cv;

            return new UpdateInfo
            {
                UpdateAvailable = isNewer,
                LatestVersion = dto.Latest,
                Notes = dto.Notes,
                DownloadUrl = dto.DownloadUrl,
                Sha256 = dto.Sha256,
                Size = dto.Size,
            };
        }
        catch (Exception ex)
        {
            _log.LogWarning("检查更新失败：{Ex}", ex.Message);
            return new UpdateInfo { UpdateAvailable = false, LatestVersion = CurrentVersion, Notes = "检查更新失败：" + ex.Message };
        }
    }

    /// <summary>下载更新包到 downloads 目录，校验 sha256。返回下载路径。</summary>
    public async Task<(bool ok, string? path, string? error)> DownloadAsync(
        UpdateInfo info, IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        if (info == null || string.IsNullOrWhiteSpace(info.DownloadUrl))
            return (false, null, "无可用下载地址");
        var dir = FileUtil.EnsureDir(Path.Combine(Paths.CacheRoot, "updates"));
        var fileName = $"vtc-client-{info.LatestVersion}.zip";
        var dest = Path.Combine(dir, fileName);
        var (ok, path, err) = await _downloader.DownloadToFileAsync(
            info.DownloadUrl, info.Sha256 ?? "", dest, progress, ct: ct);
        return (ok, path, err);
    }

    private sealed class UpdateCheckDto
    {
        public string Latest { get; set; } = "";
        public string? Notes { get; set; }
        public string? DownloadUrl { get; set; }
        public string? Sha256 { get; set; }
        public long Size { get; set; }
    }
}

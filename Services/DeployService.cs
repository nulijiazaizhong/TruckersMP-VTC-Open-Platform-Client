using System.IO;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using VTC.Shared;
using VTC.Shared.Dtos;

namespace VTC.Client.Services;

/// <summary>
/// 首页"部署存档"一键流程（C1 核心交互，规划 §3.1）。
/// 串联：下载 → 部署 → 3C 同步 → DLC 裁剪；卸载：反向还原 + 校验。
/// </summary>
public sealed class DeployService
{
    private readonly IApiClient _api;
    private readonly AuthService _auth;
    private readonly SaveDownloader _downloader;
    private readonly ThreeCSyncService _threeC;
    private readonly DlcPruneService _dlc;
    private readonly AppSettings _settings;
    private readonly IConfiguration _cfg;
    private readonly ILogger<DeployService> _log;

    public DeployService(IApiClient api, AuthService auth, SaveDownloader downloader,
        ThreeCSyncService threeC, DlcPruneService dlc, AppSettings settings,
        IConfiguration cfg, ILogger<DeployService> log)
    {
        _api = api; _auth = auth; _downloader = downloader; _threeC = threeC;
        _dlc = dlc; _settings = settings; _cfg = cfg; _log = log;
    }

    public sealed class DeployReport
    {
        public bool Ok { get; init; }
        public string Stage { get; init; } = "";
        public string? Error { get; init; }
        public string? ProfileName { get; init; }
        public string? DeployedPath { get; init; }
        public string? DlcStashDir { get; init; }
        public string? ThreeCBackupDir { get; init; }
        public List<string> DlcMoved { get; init; } = [];
        public List<string> DlcExempted { get; init; } = [];
        public List<string> DlcKept { get; init; } = [];
        public string? SourceProfile { get; init; }
        public List<string> ThreeCCopied { get; init; } = [];
    }

    /// <summary>
    /// 一键部署活动存档。
    /// </summary>
    /// <param name="evt">活动（含 SaveId 与 RequiresMapDlc 豁免标签）</param>
    /// <param name="game">ets2 | ats</param>
    /// <param name="gameVersion">完整游戏版本（如 1.61.1.9s），用于存档列表匹配</param>
    /// <param name="progress">下载进度回调</param>
    public async Task<DeployReport> DeployAsync(
        EventDto evt, string game, string gameVersion,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        // 前置检查
        var saveDir = _settings.SaveDir;
        if (string.IsNullOrWhiteSpace(saveDir) || !Directory.Exists(saveDir))
            return Fail("未设置'存档目录'，请先到设置页选择 SaveData 文件夹", "validate");

        var gameDir = _settings.GameDir;
        if (string.IsNullOrWhiteSpace(gameDir) || !Directory.Exists(gameDir))
            return Fail("未设置'游戏目录'（DLC 裁剪需要），请先到设置页选择", "validate");

        if (evt.SaveId == null)
            return Fail("该活动未关联存档（saveId 为空），无法部署", "validate");

        // ① 拉取该活动关联存档的直传凭证
        var save = await ResolveSaveAsync(evt.SaveId.Value, game, gameVersion, ct);
        if (save == null)
            return Fail("未找到该活动关联的已发布存档（saveId=" + evt.SaveId + "）", "resolve");

        // 推断 profile 名：优先活动名，回退存档 label / saveId
        var profileName = FileUtil.ProfileNameFromPackage(
            string.IsNullOrWhiteSpace(evt.Name) ? (save.Label + ".zip") : evt.Name + ".zip");

        var pkgName = $"{profileName}.zip";
        var downloadDir = Path.Combine(Paths.CacheRoot, "downloads");
        var pkgPath = Path.Combine(downloadDir, pkgName);

        // ② 分片下载并校验 sha256
        _log.LogInformation("开始下载活动存档：{Save}（{Game} {Ver}）", save.Label, game, save.GameVersion);
        var (ok, finalPath, err) = await _downloader.DownloadToFileAsync(
            save.DownloadUrl ?? "", save.Sha256, pkgPath, progress, ct: ct);
        if (!ok)
            return Fail($"存档下载失败：{err}", "download");

        // ③ 部署到存档目录下对应 profile 文件夹
        var deployed = _downloader.MaterializePackage(finalPath, Path.Combine(saveDir, profileName));
        _log.LogInformation("存档部署完成：{Profile} → {Path}", profileName, deployed);

        // ④ 3C 自动同步（从最新非活动存档 455453324C41/ 复制三文件，覆盖前备份）
        var threeCResult = _threeC.Sync(saveDir, profileName);
        if (!threeCResult.Ok)
            _log.LogWarning("3C 同步未执行：{Err}", threeCResult.Error);

        // ⑤ DLC 自动禁用（整名匹配 + move + manifest + 豁免）
        var major = ExtractMajor(gameVersion);
        var template = await _dlc.GetMapTemplateAsync(game, major);
        if (template.Count == 0)
            return Fail($"未获取到地图 DLC 模板（{game} {major}），无法裁剪（请检查 map-dlc/ 或服务端）", "dlc");

        var exempt = string.IsNullOrWhiteSpace(evt.RequiresMapDlc)
            ? null
            : evt.RequiresMapDlc.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var plan = _dlc.BuildPlan(gameDir, game, major, template, exempt);
        var (dlcOk, stashDir, dlcErr) = _dlc.Apply(plan);
        if (!dlcOk)
        {
            // DLC 裁剪失败：回滚已部署存档 + 3C，避免留下"半部署"状态
            _log.LogError("DLC 裁剪失败，回滚本次部署：{Err}", dlcErr);
            _threeC.Restore(saveDir, profileName, threeCResult.BackupDir);
            TryRemoveDeployed(deployed);
            return Fail($"DLC 裁剪失败：{dlcErr}（已回滚部署与 3C）", "dlc");
        }

        // ⑥ 记录活动部署状态（卸载时还原用）
        _settings.RecordActiveDeployment(profileName, stashDir, threeCResult.BackupDir, deployed);

        _log.LogInformation("一键部署完成：活动 {Evt}，profile={Profile}，DLC move {N} 个，3C 复制 {M} 个",
            evt.Name, profileName, plan.ToMove.Count, threeCResult.Copied.Count);

        return new DeployReport
        {
            Ok = true,
            Stage = "done",
            ProfileName = profileName,
            DeployedPath = deployed,
            DlcStashDir = stashDir,
            ThreeCBackupDir = threeCResult.Ok ? threeCResult.BackupDir : null,
            DlcMoved = plan.ToMove.Select(Path.GetFileName).ToList(),
            DlcExempted = plan.Exempted,
            DlcKept = plan.Kept,
            SourceProfile = threeCResult.SourceProfile,
            ThreeCCopied = threeCResult.Copied,
        };
    }

    /// <summary>
    /// 卸载活动存档：DLC 还原 + 校验 → 3C 回滚 → 移除部署目录 → 清状态。
    /// </summary>
    public async Task<DeployReport> UninstallAsync(IProgress<string>? stage = null, CancellationToken ct = default)
    {
        var profile = _settings.ActiveEventProfile;
        if (string.IsNullOrWhiteSpace(profile))
            return Fail("当前没有已部署的活动存档可卸载", "validate");

        var saveDir = _settings.SaveDir;
        if (string.IsNullOrWhiteSpace(saveDir))
            return Fail("存档目录未设置", "validate");

        // ① DLC 还原（按 manifest 原样还原 + 校验 sha256）
        var stashDir = _settings.ActiveDlcStashDir;
        if (!string.IsNullOrWhiteSpace(stashDir) && Directory.Exists(stashDir))
        {
            stage?.Report("正在还原 DLC…");
            var (ok, err, restored) = _dlc.Restore(stashDir);
            if (!ok)
                return Fail($"DLC 还原失败：{err}", "dlc-restore");
            _dlc.MarkRestored(stashDir);
            _log.LogInformation("DLC 还原完成：{N} 个", restored.Count);
        }

        // ② 3C 回滚（把活动存档原三文件还原）
        var threeCBak = _settings.Active3CBackupDir;
        if (!string.IsNullOrWhiteSpace(threeCBak) && Directory.Exists(threeCBak))
        {
            stage?.Report("正在回滚 3C…");
            _threeC.Restore(saveDir, profile, threeCBak);
        }

        // ③ 移除部署的存档目录
        stage?.Report("正在移除部署的存档…");
        var deployed = _settings.ActiveDeployedPath;
        if (!string.IsNullOrWhiteSpace(deployed) && Directory.Exists(deployed))
            TryRemoveDeployed(deployed);

        // ④ 清状态
        _settings.ClearActiveDeployment();
        _log.LogInformation("活动存档卸载完成：{Profile}", profile);

        return new DeployReport { Ok = true, Stage = "uninstalled", ProfileName = profile };
    }

    private async Task<SaveFileDto?> ResolveSaveAsync(Guid saveId, string game, string gameVersion, CancellationToken ct)
    {
        // 直接按 id 取详情（契约：GET /events/{id} 含关联存档；这里用 saves 列表过滤）
        try
        {
            var list = await _api.GetAsync<PageResult<SaveFileDto>>(VtcApi.Saves,
                new Dictionary<string, string?> { ["game"] = game, ["take"] = "50", ["skip"] = "0" });
            var match = list?.Items.FirstOrDefault(s => s.Id == saveId);
            if (match != null && !string.IsNullOrWhiteSpace(match.DownloadUrl))
                return match;
        }
        catch (Exception ex)
        {
            _log.LogWarning("拉取存档列表失败：{Ex}", ex.Message);
        }

        // 回退：直接向该存档要下载凭证（服务端按 id 签发）
        try
        {
            var cred = await _api.PostAsync<SaveDownloadCredentialDto>(
                VtcApi.SaveDownloadCredential.Replace("{id}", saveId.ToString()),
                new { game });
            if (cred != null && !string.IsNullOrWhiteSpace(cred.DownloadUrl))
            {
                return new SaveFileDto
                {
                    Id = saveId,
                    Label = "活动存档",
                    Game = game,
                    GameVersion = gameVersion,
                    DownloadUrl = cred.DownloadUrl,
                    Sha256 = cred.Sha256,
                };
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning("拉取下载凭证失败：{Ex}", ex.Message);
        }
        return null;
    }

    private static string ExtractMajor(string gameVersion)
    {
        // 1.61.1.9s → 1.61
        var parts = gameVersion.Split('.');
        return parts.Length >= 2 ? $"{parts[0]}.{parts[1]}" : (parts.Length == 1 ? parts[0] : "1.61");
    }

    private void TryRemoveDeployed(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
            else if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            _log.LogWarning("移除部署目录失败 {Path}：{Ex}", path, ex.Message);
        }
    }

    private static DeployReport Fail(string msg, string stage) =>
        new() { Ok = false, Error = msg, Stage = stage };
}

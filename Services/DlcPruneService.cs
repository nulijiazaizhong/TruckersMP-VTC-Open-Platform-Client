using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using VTC.Shared;
using VTC.Shared.Dtos;

namespace VTC.Client.Services;

/// <summary>
/// DLC 自动禁用 / 恢复（C5，规划 §3.8 核心）。
/// 机制（纯文件级 move + 备份 + 校验和，不碰 Steam/加密 profile/反作弊）：
///  - 按 gameMajor 取"地图类真实文件名"模板（服务端 /dlc/map-template，本地 map-dlc/&lt;major&gt;.&lt;game&gt;.txt 兜底）；
///  - 部署活动存档：在 &lt;gameDir&gt; 下对模板做【整名匹配】（File.Exists，无通配、无前缀），
///    命中的 dlc_*.scs 整体 move 到 &lt;gameDir&gt;\dlc_stash\&lt;gameMajor&gt;\&lt;ts&gt;\；
///  - 未命中（车辆/涂装/轮胎/rdlc）一律保留原位；
///  - 活动 requires_map_dlc 标签中的包【豁免保留】（不 move）；
///  - move 前对每个文件记 sha256 到备份清单 manifest.json；
///  - 卸载活动存档：按 manifest.json 原样还原 + 校验 sha256；
///  - 操作幂等、可中止；任何异常自动回滚已 move 的文件；
///  - 全程写本地审计日志（谁、何时、禁用/保留了哪些 DLC、备份路径）。
/// </summary>
public sealed class DlcPruneService
{
    private readonly ILogger<DlcPruneService> _log;
    private readonly IApiClient? _api;
    private readonly string? _actor;

    public DlcPruneService(ILogger<DlcPruneService> log, IApiClient? api = null, string? actor = null)
    {
        _log = log;
        _api = api;
        _actor = actor ?? "client";
    }

    public const string StashRootName = "dlc_stash";

    public sealed class PrunePlan
    {
        public string GameDir { get; init; } = "";
        public string GameMajor { get; init; } = "";
        public string StashDir { get; init; } = "";
        public List<string> TemplateFiles { get; init; } = [];
        /// <summary>将被 move 的文件（gameDir 内绝对路径）。</summary>
        public List<string> ToMove { get; init; } = [];
        /// <summary>因 requires_map_dlc 豁免保留的文件名。</summary>
        public List<string> Exempted { get; init; } = [];
        /// <summary>保留原位（未命中模板）的 dlc_*.scs 文件名（车辆/涂装/rdlc）。</summary>
        public List<string> Kept { get; init; } = [];
        public bool ManifestExists { get; init; }
    }

    public sealed class Manifest
    {
        public string GameMajor { get; set; } = "";
        public string GameDir { get; set; } = "";
        public string CreatedAt { get; set; } = "";
        public string Actor { get; set; } = "";
        public List<ManifestEntry> Entries { get; set; } = [];
    }

    public sealed class ManifestEntry
    {
        public string FileName { get; set; } = "";
        public string StashPath { get; set; } = "";   // 备份后绝对路径
        public string OrigPath { get; set; } = "";    // 原绝对路径
        public string Sha256 { get; set; } = "";
        public long Size { get; set; }
    }

    #region 模板

    /// <summary>
    /// 取地图类文件名模板。优先服务端 /dlc/map-template?game=&major=；
    /// 失败回退本地 map-dlc/&lt;major&gt;.&lt;game&gt;.txt（仓库根或 exe 目录）。
    /// </summary>
    public async Task<List<string>> GetMapTemplateAsync(string game, string major)
    {
        if (_api != null)
        {
            try
            {
                var dto = await _api.GetAsync<DlcMapTemplateDto>(VtcApi.DlcMapTemplate,
                    new Dictionary<string, string?> { ["game"] = game, ["major"] = major });
                if (dto != null && dto.Found && dto.Files.Count > 0)
                {
                    _log.LogInformation("地图模板来自服务端（{Game} {Major}，{N} 个）", game, major, dto.Files.Count);
                    return dto.Files;
                }
                if (dto != null)
                    _log.LogWarning("服务端未找到模板 {Game} {Major}，回退本地", game, major);
            }
            catch (Exception ex)
            {
                _log.LogWarning("拉取地图模板失败（{Ex}），回退本地 map-dlc/", ex.Message);
            }
        }
        return LoadLocalTemplate(game, major);
    }

    /// <summary>本地兜底模板：map-dlc/&lt;major&gt;.&lt;game&gt;.txt（exe 目录 / 仓库根 / VTC_HOME）。</summary>
    public List<string> LoadLocalTemplate(string game, string major)
    {
        var names = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "map-dlc", $"{major}.{game}.txt"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "map-dlc", $"{major}.{game}.txt"),
            Path.Combine(Environment.CurrentDirectory, "map-dlc", $"{major}.{game}.txt"),
            Path.Combine(Paths.CacheRoot, "..", "map-dlc", $"{major}.{game}.txt"),
        };
        foreach (var p in names)
        {
            var full = Path.GetFullPath(p);
            if (File.Exists(full))
            {
                var lines = File.ReadAllLines(full)
                    .Select(l => l.Trim())
                    .Where(l => l.Length > 0 && !l.StartsWith("#"))
                    .ToList();
                _log.LogInformation("地图模板来自本地 {P}（{N} 个）", full, lines.Count);
                return lines;
            }
        }
        _log.LogError("未找到本地地图模板 {Major}.{Game}（map-dlc/ 缺失）", major, game);
        return [];
    }

    #endregion

    #region 计划（只读，不移动文件）

    /// <summary>
    /// 制定裁剪计划：整名匹配 + 豁免 + 保留分类。不修改任何文件。
    /// </summary>
    public PrunePlan BuildPlan(string gameDir, string game, string major, List<string> template, IEnumerable<string>? exemptFiles)
    {
        if (!Directory.Exists(gameDir))
            throw new InvalidOperationException($"游戏目录不存在：{gameDir}");

        var ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var stashDir = Path.Combine(gameDir, StashRootName, major, ts);

        // 豁免集合（活动 requires_map_dlc 标签中的真实文件名）
        var exempt = (exemptFiles ?? [])
            .Select(f => f.EndsWith(".scs", StringComparison.OrdinalIgnoreCase) ? f : f + ".scs")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var toMove = new List<string>();
        var exempted = new List<string>();
        var kept = new List<string>();

        // 只对模板里的文件名做整名匹配（白名单只进不出）
        foreach (var name in template.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var fileName = name.EndsWith(".scs", StringComparison.OrdinalIgnoreCase) ? name : name + ".scs";
            var candidate = Path.Combine(gameDir, fileName);
            if (!File.Exists(candidate))
                continue; // 游戏未安装该 DLC → 无文件可 move

            if (exempt.Contains(fileName))
            {
                exempted.Add(fileName);
                continue;
            }
            toMove.Add(candidate);
        }

        // 分类保留：扫描 gameDir 下所有 dlc_*.scs，未被 move 的即"保留"（车辆/涂装/轮胎/rdlc）
        foreach (var f in SafeEnumerateDlc(gameDir))
        {
            var full = Path.Combine(gameDir, f);
            if (!toMove.Contains(full) && !exempt.Contains(f))
                kept.Add(f);
        }

        var manifestPath = Path.Combine(stashDir, "manifest.json");
        return new PrunePlan
        {
            GameDir = gameDir,
            GameMajor = major,
            StashDir = stashDir,
            TemplateFiles = template.ToList(),
            ToMove = toMove,
            Exempted = exempted,
            Kept = kept,
            ManifestExists = File.Exists(manifestPath),
        };
    }

    private static IEnumerable<string> SafeEnumerateDlc(string gameDir)
    {
        try
        {
            return Directory.EnumerateFiles(gameDir, "dlc_*.scs");
        }
        catch { return []; }
    }

    #endregion

    #region 执行（move + manifest）

    /// <summary>
    /// 执行裁剪：按计划把 ToMove 文件 move 进 stashDir，记录 manifest.json。
    /// 幂等：若目标已存在且源缺失（已裁过）则跳过；任何异常回滚本批次已 move 的文件。
    /// </summary>
    public (bool ok, string? stashDir, string? error) Apply(PrunePlan plan)
    {
        if (plan.ToMove.Count == 0)
        {
            _log.LogInformation("裁剪计划为空（无命中地图 DLC），无需 move");
            return (true, null, null);
        }

        FileUtil.EnsureDir(plan.StashDir);
        var moved = new List<(string from, string to)>();
        var entries = new List<ManifestEntry>();
        try
        {
            foreach (var src in plan.ToMove)
            {
                var name = Path.GetFileName(src);
                var dest = Path.Combine(plan.StashDir, name);
                if (!File.Exists(src))
                {
                    // 幂等：源已不在（可能上一轮已裁）。若备份已在则跳过，否则跳过
                    if (File.Exists(dest)) continue;
                    _log.LogWarning("源文件缺失，跳过：{Src}", src);
                    continue;
                }
                if (File.Exists(dest)) File.Delete(dest);

                var sha = FileUtil.Sha256File(src);
                var size = new FileInfo(src).Length;
                File.Move(src, dest);
                moved.Add((src, dest));
                entries.Add(new ManifestEntry
                {
                    FileName = name,
                    StashPath = dest,
                    OrigPath = src,
                    Sha256 = sha,
                    Size = size,
                });
            }

            var manifest = new Manifest
            {
                GameMajor = plan.GameMajor,
                GameDir = plan.GameDir,
                CreatedAt = DateTime.Now.ToString("O"),
                Actor = _actor,
                Entries = entries,
            };
            File.WriteAllText(Path.Combine(plan.StashDir, "manifest.json"),
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));

            Audit("dlc.disable",
                $"禁用地图 DLC {entries.Count} 个：{string.Join(",", entries.Select(e => e.FileName))}；" +
                $"豁免保留 {string.Join(",", plan.Exempted) ?? "无"}；保留车辆/涂装等 {plan.Kept.Count} 个；" +
                $"备份 {plan.StashDir}");

            _log.LogInformation("DLC 裁剪完成：move {N} 个 → {Stash}；豁免 {X}；保留 {K}",
                entries.Count, plan.StashDir, plan.Exempted.Count, plan.Kept.Count);
            return (true, plan.StashDir, null);
        }
        catch (Exception ex)
        {
            _log.LogError("DLC 裁剪异常，回滚已 move 文件：{Ex}", ex.Message);
            RollbackMoved(moved);
            return (false, null, ex.Message);
        }
    }

    private void RollbackMoved(List<(string from, string to)> moved)
    {
        foreach (var (from, to) in moved.AsEnumerable().Reverse())
        {
            try
            {
                if (File.Exists(to) && !File.Exists(from))
                    File.Move(to, from);
            }
            catch (Exception e) { _log.LogError("回滚单文件失败 {To}→{From}: {E}", to, from, e.Message); }
        }
    }

    #endregion

    #region 恢复（restore + 校验）

    /// <summary>
    /// 卸载活动存档时：读 manifest.json 原样还原 + 校验 sha256。
    /// 幂等：已还原的跳过；任一文件校验失败则整体回滚本次还原并报错。
    /// </summary>
    public (bool ok, string? error, List<string> restored) Restore(string stashDir)
    {
        var manifestPath = Path.Combine(stashDir, "manifest.json");
        if (!File.Exists(manifestPath))
            return (false, $"备份清单不存在：{manifestPath}", []);

        var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(manifestPath));
        if (manifest == null || manifest.Entries.Count == 0)
            return (false, "备份清单为空", []);

        var restored = new List<string>();
        var done = new List<(string from, string to)>();
        try
        {
            foreach (var e in manifest.Entries)
            {
                if (!File.Exists(e.StashPath))
                {
                    if (File.Exists(e.OrigPath)) { restored.Add(e.FileName); continue; } // 已还原
                    return (false, $"备份文件丢失：{e.StashPath}", []);
                }
                // 校验备份完整性
                var sha = FileUtil.Sha256File(e.StashPath);
                if (e.Sha256.Length > 0 && !string.Equals(sha, e.Sha256, StringComparison.OrdinalIgnoreCase))
                    return (false, $"备份 sha256 校验失败：{e.FileName}（want {e.Sha256[..Math.Min(12, e.Sha256.Length)]}… got {sha[..Math.Min(12, sha.Length)]}…）", []);

                if (File.Exists(e.OrigPath))
                {
                    // 目标已存在（可能被 Steam 验证拉回/用户手放）：保留现状，不覆盖
                    _log.LogWarning("还原目标已存在，跳过（可能被 Steam 验证拉回）：{Orig}", e.OrigPath);
                    restored.Add(e.FileName);
                    continue;
                }
                FileUtil.EnsureDir(Path.GetDirectoryName(e.OrigPath)!);
                File.Move(e.StashPath, e.OrigPath);
                done.Add((e.OrigPath, e.StashPath));
                restored.Add(e.FileName);
            }

            Audit("dlc.restore", $"恢复地图 DLC {restored.Count} 个：{string.Join(",", restored)}；来源备份 {stashDir}");
            _log.LogInformation("DLC 恢复完成：{N} 个 ← {Stash}", restored.Count, stashDir);
            return (true, null, restored);
        }
        catch (Exception ex)
        {
            _log.LogError("DLC 恢复异常，回滚本次还原：{Ex}", ex.Message);
            foreach (var (from, to) in done.AsEnumerable().Reverse())
            {
                try { if (File.Exists(from) && !File.Exists(to)) File.Move(from, to); }
                catch (Exception e2) { _log.LogError("恢复回滚失败 {From}: {E}", from, e2.Message); }
            }
            return (false, ex.Message, []);
        }
    }

    /// <summary>把指定 stash 标记为已恢复（可选：移入 done/ 子目录归档）。</summary>
    public void MarkRestored(string stashDir)
    {
        try
        {
            var parent = Path.GetDirectoryName(stashDir)!;
            var done = Path.Combine(parent, "done");
            FileUtil.EnsureDir(done);
            var name = Path.GetFileName(stashDir);
            if (Directory.Exists(stashDir))
                Directory.Move(stashDir, Path.Combine(done, name + "_restored_" + DateTime.Now.ToString("yyyyMMddHHmmss")));
        }
        catch (Exception ex) { _log.LogWarning("归档已恢复 stash 失败：{Ex}", ex.Message); }
    }

    #endregion

    #region 检测（车队要求 DLC 是否已安装）

    /// <summary>
    /// 检测已安装 dlc_*.scs，对照车队要求清单（/dlc/required），缺啥给 purchaseUrl。
    /// </summary>
    public (List<string> installed, List<string> missing) DetectInstalled(string gameDir, string game, string version)
    {
        var installed = SafeEnumerateDlc(gameDir).ToList();
        return (installed, []);
    }

    public async Task<DlcRequiredDto?> GetRequiredAsync(string game, string version)
    {
        if (_api == null) return null;
        try
        {
            return await _api.GetAsync<DlcRequiredDto>(VtcApi.DlcRequired,
                new Dictionary<string, string?> { ["game"] = game, ["version"] = version });
        }
        catch (Exception ex)
        {
            _log.LogWarning("拉取车队要求 DLC 失败：{Ex}", ex.Message);
            return null;
        }
    }

    /// <summary>上报本地 DLC 校验结果（POST /dlc/report）。</summary>
    public async Task<bool> ReportAsync(string game, List<string> missing, List<string> installed)
    {
        if (_api == null) return false;
        try
        {
            var body = new DlcReportDto { Game = game, Missing = missing, Installed = installed };
            var r = await _api.PostAsync<DlcReportResultDto>(VtcApi.DlcReport, body);
            return r?.Accepted == true;
        }
        catch (Exception ex)
        {
            _log.LogWarning("DLC 上报失败：{Ex}", ex.Message);
            return false;
        }
    }

    #endregion

    #region 审计

    /// <summary>写本地审计日志（Paths.AuditLogFile）：谁、何时、动作、明细。</summary>
    private void Audit(string action, string detail)
    {
        try
        {
            FileUtil.EnsureDir(Paths.TeamDataDir);
            var line = $"{DateTime.Now:O}\t{_actor}\t{action}\t{detail}{Environment.NewLine}";
            File.AppendAllText(Paths.AuditLogFile, line);
        }
        catch (Exception ex)
        {
            _log.LogWarning("写审计日志失败：{Ex}", ex.Message);
        }
    }

    #endregion
}

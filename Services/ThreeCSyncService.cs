using System.IO;
using Microsoft.Extensions.Logging;
using VTC.Shared;

namespace VTC.Client.Services;

/// <summary>
/// 3C 自动同步（C6/§3.6）。
/// 部署活动存档时：从"最新的非活动存档"的 455453324C41/ 复制
/// config.cfg / config_local.cfg / controls.sii 三文件到活动存档同名目录。
/// 覆盖前备份活动存档原有三文件（.vtc3c/&lt;ts&gt;/），可回滚；只碰 455453324C41/ 三文件，不碰任意 mods。
/// </summary>
public sealed class ThreeCSyncService
{
    private readonly ILogger<ThreeCSyncService> _log;
    public const string VtcDir = "455453324C41";
    public static readonly string[] ThreeCFiles = LocalPaths.ThreeCFiles;

    public ThreeCSyncService(ILogger<ThreeCSyncService> log) => _log = log;

    public sealed class SyncResult
    {
        public bool Ok { get; init; }
        public string? SourceProfile { get; init; }
        public string BackupDir { get; init; } = "";
        public List<string> Copied { get; init; } = [];
        public List<string> MissingInSource { get; init; } = [];
        public string? Error { get; init; }
    }

    /// <summary>
    /// 执行 3C 同步。
    /// </summary>
    /// <param name="saveDir">设置页所选"存档目录"（含各 profile 存档文件夹）</param>
    /// <param name="eventProfileName">本次部署的活动存档 profile 名（用于排除自身并定位备份）</param>
    public SyncResult Sync(string saveDir, string eventProfileName)
    {
        if (!Directory.Exists(saveDir))
            return new SyncResult { Ok = false, Error = $"存档目录不存在：{saveDir}" };

        // 1) 找"最新的非活动存档"：按目录 LastWriteTime 降序，排除活动存档自身
        var sourceDir = Directory.EnumerateDirectories(saveDir)
            .Where(d =>
            {
                var name = Path.GetFileName(d);
                return !name.Equals(eventProfileName, StringComparison.OrdinalIgnoreCase) &&
                       !name.StartsWith(".", StringComparison.Ordinal) &&
                       FileUtil.LooksLikeProfileSave(d);
            })
            .OrderByDescending(d =>
            {
                try { return Directory.GetLastWriteTime(d); }
                catch { return DateTime.MinValue; }
            })
            .FirstOrDefault();

        if (sourceDir == null)
            return new SyncResult { Ok = false, Error = "未找到可用作 3C 来源的'最新非活动存档'（需含 info.sii 或 455453324C41/）" };

        var srcVtc = Path.Combine(sourceDir, VtcDir);
        if (!Directory.Exists(srcVtc))
            return new SyncResult { Ok = false, Error = $"来源存档缺少 {VtcDir}/ 目录：{srcVtc}" };

        // 2) 活动存档目标 455453324C41/
        var targetDir = Path.Combine(saveDir, eventProfileName);
        FileUtil.EnsureDir(targetDir);
        var dstVtc = FileUtil.EnsureDir(Path.Combine(targetDir, VtcDir));

        // 3) 覆盖前备份活动存档原有三文件
        var ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var backupDir = FileUtil.EnsureDir(Path.Combine(targetDir, ".vtc3c", ts));
        var copied = new List<string>();
        var missing = new List<string>();

        foreach (var f in ThreeCFiles)
        {
            var src = Path.Combine(srcVtc, f);
            if (!File.Exists(src))
            {
                missing.Add(f);
                continue;
            }

            var dst = Path.Combine(dstVtc, f);
            // 备份原文件（若存在）
            if (File.Exists(dst))
            {
                File.Copy(dst, Path.Combine(backupDir, f), overwrite: true);
            }
            File.Copy(src, dst, overwrite: true);
            copied.Add(f);
        }

        if (copied.Count == 0)
            return new SyncResult
            {
                Ok = false,
                SourceProfile = Path.GetFileName(sourceDir),
                BackupDir = backupDir,
                MissingInSource = missing,
                Error = $"来源存档 {VtcDir}/ 下无任一 3C 文件（{string.Join(",", missing)}）",
            };

        _log.LogInformation("3C 同步完成：来源 {Src} → 活动 {Evt}，复制 {N} 文件，备份 {Bk}",
            Path.GetFileName(sourceDir), eventProfileName, copied.Count, backupDir);

        return new SyncResult
        {
            Ok = true,
            SourceProfile = Path.GetFileName(sourceDir),
            BackupDir = backupDir,
            Copied = copied,
            MissingInSource = missing,
        };
    }

    /// <summary>回滚：把备份目录里的三文件还原到活动存档（卸载活动存档时调用）。</summary>
    public bool Restore(string saveDir, string eventProfileName, string backupDir)
    {
        if (!Directory.Exists(backupDir)) return false;
        var dstVtc = Path.Combine(saveDir, eventProfileName, VtcDir);
        foreach (var f in ThreeCFiles)
        {
            var bak = Path.Combine(backupDir, f);
            if (!File.Exists(bak)) continue;
            FileUtil.EnsureDir(dstVtc);
            File.Copy(bak, Path.Combine(dstVtc, f), overwrite: true);
        }
        _log.LogInformation("3C 回滚完成：{Bk} → {Evt}", backupDir, eventProfileName);
        return true;
    }
}

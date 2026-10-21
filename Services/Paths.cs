using System.IO;

namespace VTC.Client;

/// <summary>
/// 客户端本地路径（p0-contract §7）。
/// 数据根：APPDATA/VTC（Windows）、~/.config/VTC（Linux）、~/Library/Application Support/VTC（macOS）。
/// 多车队：子目录按 team-slug（小写用户名段）隔离，如 tokens.json。
/// </summary>
public static class Paths
{
    public static string AppDataRoot { get; private set; } = DefaultAppData();
    public static string TeamSlug { get; set; } = "default";
    public static string TeamDataDir => Path.Combine(AppDataRoot, SafeSlug(TeamSlug));
    public static string CacheRoot { get; private set; } = DefaultCache();
    public static string LogFile => Path.Combine(TeamDataDir, "vtc.log");
    public static string AuditLogFile => Path.Combine(TeamDataDir, "dlc_audit.log");
    public static string TokensFile => Path.Combine(TeamDataDir, "tokens.json");
    public static string ReadIdsFile => Path.Combine(TeamDataDir, "read_ids.json");
    public static string SettingsFile => Path.Combine(TeamDataDir, "settings.json");
    public static string LastConfigFile => Path.Combine(TeamDataDir, "public_config.json");
    public static string DlcStashRoot => "<GAME_DIR>"; // 占位：实际为 {gameDir}\dlc_stash（见 DlcPruneService）

    static Paths()
    {
        EnsureDir(TeamDataDir);
        EnsureDir(CacheRoot);
    }

    public static void SetTeamSlug(string slug)
    {
        if (string.IsNullOrWhiteSpace(slug)) slug = "default";
        TeamSlug = SafeSlug(slug);
        EnsureDir(TeamDataDir);
        EnsureDir(CacheRoot);
    }

    private static string SafeSlug(string slug)
    {
        var s = slug.Trim().ToLowerInvariant();
        var sb = new System.Text.StringBuilder();
        foreach (var c in s)
            sb.Append(char.IsLetterOrDigit(c) || c == '-' ? c : '_');
        return sb.Length == 0 ? "default" : sb.ToString();
    }

    public static void EnsureDir(string dir)
    {
        if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
    }

    private static string DefaultAppData()
    {
        // 调试/可移植重定向：VTC_DATA_DIR 环境变量优先（如 VS Code 调试时指向工作区，
        // 规避调试宿主对 %APPDATA% 的 ACL 限制）。生产不设置，走默认 %APPDATA%/VTC。
        var redirect = Environment.GetEnvironmentVariable("VTC_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(redirect))
            return redirect;

        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrEmpty(baseDir))
            baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        return Path.Combine(baseDir, "VTC");
    }

    private static string DefaultCache()
    {
        // 重定向：设了 VTC_DATA_DIR 时，缓存落在数据根下，避免调试宿主对 %LOCALAPPDATA% 的 ACL 限制
        var redirect = Environment.GetEnvironmentVariable("VTC_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(redirect))
            return Path.Combine(redirect, "cache");

        // Windows: %LOCALAPPDATA%\VTC\cache；其它: ~/.cache/vtc
        var local = OperatingSystem.IsWindows()
            ? Environment.GetEnvironmentVariable("LOCALAPPDATA")
            : null;
        if (!string.IsNullOrEmpty(local))
            return Path.Combine(local, "VTC", "cache");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".cache", "vtc");
    }
}

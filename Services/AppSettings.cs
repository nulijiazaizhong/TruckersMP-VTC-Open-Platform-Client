using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace VTC.Client.Services;

/// <summary>
/// 应用设置（C12）：存档目录 / 游戏目录 / 语言 / 主题。
/// 持久化：Paths.SettingsFile（JSON）。目录选择可空（未设置时功能降级并提示）。
/// </summary>
public sealed class AppSettings
{
    private readonly ILogger<AppSettings> _log;
    private readonly object _gate = new();
    private AppSettingsData _data;

    public string? SaveDir { get => _data.SaveDir; private set => _data.SaveDir = value; }
    public string? GameDir { get => _data.GameDir; private set => _data.GameDir = value; }
    public string GameDocDir { get => _data.GameDocDir; private set => _data.GameDocDir = value; }
    public string Lang { get => _data.Lang; private set => _data.Lang = value; }
    public string Theme { get => _data.Theme; private set => _data.Theme = value; }
    /// <summary>当前活动存档 profile 名（部署后记录，卸载时还原用）。</summary>
    public string? ActiveEventProfile { get => _data.ActiveEventProfile; private set => _data.ActiveEventProfile = value; }
    /// <summary>活动存档的 DLC 备份目录（卸载还原用）。</summary>
    public string? ActiveDlcStashDir { get => _data.ActiveDlcStashDir; private set => _data.ActiveDlcStashDir = value; }
    /// <summary>活动存档 3C 备份目录。</summary>
    public string? Active3CBackupDir { get => _data.Active3CBackupDir; private set => _data.Active3CBackupDir = value; }
    /// <summary>活动存档原始包备份（卸载时可选移除部署目录）。</summary>
    public string? ActiveDeployedPath { get => _data.ActiveDeployedPath; private set => _data.ActiveDeployedPath = value; }

    public event EventHandler? Changed;

    public AppSettings(ILogger<AppSettings> log)
    {
        _log = log;
        _data = Load();
    }

    public AppSettingsData Data => _data;

    private AppSettingsData Load()
    {
        try
        {
            if (File.Exists(Paths.SettingsFile))
            {
                var d = JsonSerializer.Deserialize<AppSettingsData>(File.ReadAllText(Paths.SettingsFile));
                if (d != null) return Normalise(d);
            }
        }
        catch (Exception ex) { _log.LogWarning("读取 settings.json 失败：{Ex}", ex.Message); }
        return Normalise(new AppSettingsData());
    }

    private static AppSettingsData Normalise(AppSettingsData d)
    {
        // 清洗历史脏数据：早期版本曾把"降级/失败提示文本"当成路径写入配置，
        // 启动时识别并清空（提示文本特征：以 '(' 开头的非真实路径），避免再次显示。
        d.SaveDir = CleanPath(Expand(d.SaveDir));
        d.GameDir = CleanPath(Expand(d.GameDir));
        d.GameDocDir = CleanPath(Expand(d.GameDocDir));
        if (string.IsNullOrWhiteSpace(d.Lang)) d.Lang = "zh-CN";
        if (string.IsNullOrWhiteSpace(d.Theme)) d.Theme = "light";
        return d;
    }

    /// <summary>值是提示文本（以 '(' 开头）时清空为 null，避免把历史脏数据显示出来。</summary>
    private static string? CleanPath(string? v)
    {
        if (string.IsNullOrWhiteSpace(v)) return v;
        if (v.StartsWith('(') || v.StartsWith("（")) return null;
        return v;
    }

    static string? Expand(string? v) => string.IsNullOrWhiteSpace(v) ? v : EnvLoader.Expand(v);

    public void SetSaveDir(string dir) { lock (_gate) { SaveDir = dir; Save(); } }
    public void SetGameDir(string dir)
    {
        lock (_gate)
        {
            GameDir = dir;
            // 自动推断 GameDocDir：Windows 默认 %USERPROFILE%\Documents\Euro Truck Simulator 2
            if (string.IsNullOrWhiteSpace(GameDocDir) || !Directory.Exists(GameDocDir))
            {
                var doc = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                if (!string.IsNullOrEmpty(doc))
                {
                    // ETS2 文档目录按游戏名推断（当前 P0 仅 ETS2 默认）
                    var guess = Path.Combine(doc, "Euro Truck Simulator 2");
                    if (Directory.Exists(guess)) GameDocDir = guess;
                }
            }
            Save();
        }
    }
    public void SetGameDocDir(string dir) { lock (_gate) { GameDocDir = dir; Save(); } }
    public void SetLang(string lang) { lock (_gate) { Lang = lang; Save(); } }
    public void SetTheme(string theme) { lock (_gate) { Theme = theme; Save(); } }

    public void RecordActiveDeployment(string profile, string? dlcStash, string? threeCBackup, string deployedPath)
    {
        lock (_gate)
        {
            ActiveEventProfile = profile;
            ActiveDlcStashDir = dlcStash;
            Active3CBackupDir = threeCBackup;
            ActiveDeployedPath = deployedPath;
            Save();
        }
    }

    public void ClearActiveDeployment()
    {
        lock (_gate)
        {
            ActiveEventProfile = null;
            ActiveDlcStashDir = null;
            Active3CBackupDir = null;
            ActiveDeployedPath = null;
            Save();
        }
    }

    private void Save()
    {
        try
        {
            FileUtil.EnsureDir(Paths.TeamDataDir);
            File.WriteAllText(Paths.SettingsFile,
                JsonSerializer.Serialize(_data, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { _log.LogWarning("写 settings.json 失败：{Ex}", ex.Message); }
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

public class AppSettingsData
{
    public string? SaveDir { get; set; }
    public string? GameDir { get; set; }
    public string? GameDocDir { get; set; }
    public string Lang { get; set; } = "zh-CN";
    public string Theme { get; set; } = "light";
    public string? ActiveEventProfile { get; set; }
    public string? ActiveDlcStashDir { get; set; }
    public string? Active3CBackupDir { get; set; }
    public string? ActiveDeployedPath { get; set; }
}

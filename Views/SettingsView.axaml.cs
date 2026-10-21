using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Microsoft.Extensions.DependencyInjection;
using VTC.Client.Services;

namespace VTC.Client.Views;

public partial class SettingsView : UserControl
{
    private AppSettings _settings = null!;
    private UpdateService _update = null!;
    private IApiClient _api = null!;

    public SettingsView()
    {
        InitializeComponent();
        var sp = AppServiceLocator.Services;
        _settings = sp.GetRequiredService<AppSettings>();
        _update = sp.GetRequiredService<UpdateService>();
        _api = sp.GetRequiredService<IApiClient>();
        RefreshDirs();
        RefreshEnv();
        RefreshActive();
        _ = RefreshLogAsync(200);
    }


    private void RefreshDirs()
    {
        saveDir.Text = _settings.SaveDir ?? "";
        gameDir.Text = _settings.GameDir ?? "";
        gameDocDir.Text = _settings.GameDocDir ?? "";
    }

    private void RefreshActive()
    {
        activeProfile.Text = _settings.ActiveEventProfile ?? "（无）";
        activeDlc.Text = _settings.ActiveDlcStashDir ?? "（无）";
        uninstallBtn.IsEnabled = _settings.ActiveEventProfile != null;
    }

    private void RefreshEnv()
    {
        var cfg = AppServiceLocator.Get<Microsoft.Extensions.Configuration.IConfiguration>();
        var sensitive = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "STEAM_API_KEY", "JWT_SECRET", "S3_SECRET_KEY", "DB_CONNECTION" };
        var items = new List<(string Key, string Value)>();
        foreach (string k in new[]
        {
            "VTC_API_BASE", "VTC_WS_BASE", "TEAM_NAME", "TEAM_SLOGAN", "TEAM_LOGO",
            "LOGIN_BG", "LOGIN_LAYOUT", "ACCENT_COLOR", "AUTH_MODE", "STEAM_API_KEY",
            "TMP_SERVER_ID", "SAVE_DIR", "ALLOW_UPLOAD", "UPDATE_CHANNEL", "LOG_LEVEL",
        })
        {
            var v = cfg[k] ?? "";
            if (sensitive.Contains(k) && v.Length > 0)
                v = v.Length <= 4 ? "****" : "****" + v.Substring(v.Length - 4);
            items.Add((k, string.IsNullOrEmpty(v) ? "（未设置）" : v));
        }
        envList.ItemsSource = items;
    }

    private async Task RefreshLogAsync(int lines)
    {
        try
        {
            if (!File.Exists(Paths.LogFile))
            {
                logText.Text = "（暂无日志）";
                return;
            }
            var all = await File.ReadAllLinesAsync(Paths.LogFile);
            var start = all.Length > lines ? all.Length - lines : 0;
            var tail = System.Linq.Enumerable.Range(start, all.Length - start)
                .Select(i => all[i]);
            logText.Text = string.Join(Environment.NewLine, tail);
        }
        catch (Exception ex)
        {
            logText.Text = "读取日志失败：" + ex.Message;
        }
    }

    // ===== 事件 =====

    private void OnPickSaveDir(object? sender, RoutedEventArgs e) => PickFolder(p => { SetDirSafe(saveDir, () => { _settings.SetSaveDir(p); RefreshDirs(); }); });
    private void OnPickGameDir(object? sender, RoutedEventArgs e) => PickFolder(p => { SetDirSafe(gameDir, () => { _settings.SetGameDir(p); RefreshDirs(); RefreshActive(); }); });
    private void OnPickGameDoc(object? sender, RoutedEventArgs e) => PickFolder(p => { SetDirSafe(gameDocDir, () => { _settings.SetGameDocDir(p); RefreshDirs(); }); });

    /// <summary>校验所选值是真实路径才写入；否则只回显提示，不污染配置。</summary>
    private void SetDirSafe(TextBox target, Action apply)
    {
        var v = target.Text ?? "";
        if (v.StartsWith('(')) // 降级提示文本 → 不写配置
            return;
        apply();
    }

    private void PickFolder(Action<string> onPicked)
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
            || desktop.Windows.Count == 0) return;
        var win = desktop.Windows[0];
        var storage = win.StorageProvider;
        if (storage is null) return;
        var task = storage.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions
        {
            Title = "选择目录",
            AllowMultiple = false,
        });
        _ = ProcessFoldersAsync(task, onPicked);
    }

    private static async Task ProcessFoldersAsync(
        System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<Avalonia.Platform.Storage.IStorageFolder>> task,
        Action<string> onPicked)
    {
        var folders = await task;
        if (folders is { Count: > 0 })
        {
            var path = ExtractFolderPath(folders[0]);
            if (string.IsNullOrWhiteSpace(path))
            {
                // 选择器可用但路径提取失败：不要伪装成路径写进配置，给出明确提示
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    onPicked("(未能读取所选目录路径，请手动填写绝对路径)"));
                return;
            }
            Avalonia.Threading.Dispatcher.UIThread.Post(() => onPicked(path!));
        }
    }

    /// <summary>
    /// 从 IStorageFolder 实例提取绝对路径。Avalonia 11.3 的 IStorageItem 路径属性名
    /// 跨版本有变动（11.0 LocalPath → 11.3 可能更名），不猜名字：
    /// 遍历实例类型所有 public 只读 string 属性，取第一个"看起来像绝对路径"的值。
    /// </summary>
    private static string? ExtractFolderPath(object item)
    {
        // ① 先试几个已知属性名（快路径，覆盖 11.0-11.2 的 LocalPath）
        foreach (var name in new[] { "LocalPath", "Path", "DisplayPath", "PathString", "FullPath" })
        {
            try
            {
                if (item.GetType().GetProperty(name)?.GetValue(item) is string s && LooksLikePath(s))
                    return s;
            }
            catch { }
        }
        // ② 兜底：遍历所有 public 实例 string 属性，找第一个像路径的
        try
        {
            foreach (var p in item.GetType().GetProperties(
                         System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                if (p.PropertyType != typeof(string) || !p.CanRead) continue;
                try
                {
                    if (p.GetValue(item) is string s && LooksLikePath(s))
                        return s;
                }
                catch { }
            }
        }
        catch { }
        return null;
    }

    private static bool LooksLikePath(string s)
    {
        if (string.IsNullOrWhiteSpace(s) || s.Length < 3) return false;
        // Windows 盘符（C:\...）、UNC（\\server\share）、POSIX 绝对路径（/...）
        return s.Length >= 2 && char.IsLetter(s[0]) && s[1] == ':'
            || s.StartsWith("\\\\")
            || s.StartsWith('/');
    }

    private async void OnCheckUpdateClick(object? sender, RoutedEventArgs e)
    {
        checkUpdateBtn.IsEnabled = false;
        updateStatus.Text = "检查中…";
        try
        {
            var info = await _update.CheckAsync();
            if (info.UpdateAvailable)
                updateStatus.Text = $"发现新版本 {info.LatestVersion}（当前 {_update.CurrentVersion}）";
            else
                updateStatus.Text = $"已是最新（{_update.CurrentVersion}）";
        }
        catch (Exception ex)
        {
            updateStatus.Text = "检查失败：" + ex.Message;
        }
        finally
        {
            checkUpdateBtn.IsEnabled = true;
        }
    }

    private void OnRefreshLogClick(object? sender, RoutedEventArgs e)
    {
        var lines = new[] { 50, 100, 200, 500 }[Math.Clamp(logLines.SelectedIndex, 0, 3)];
        _ = RefreshLogAsync(lines);
    }

    private void OnUninstallClick(object? sender, RoutedEventArgs e)
    {
        // 确认对话框（不用 alert/confirm，用 Avalonia 原生对话框）
        var deploy = AppServiceLocator.Get<DeployService>();

        var okBtn = new Button { Content = "确定卸载", Width = 110, Height = 34,
            Background = Avalonia.Media.Brushes.Crimson, Foreground = Avalonia.Media.Brushes.White };
        var cancelBtn = new Button { Content = "取消", Width = 80, Height = 34 };

        var buttons = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Spacing = 10,
            Children = { cancelBtn, okBtn }
        };

        var content = new StackPanel
        {
            Margin = new Avalonia.Thickness(20),
            Spacing = 14,
            Children =
            {
                new TextBlock
                {
                    Text = "将还原 DLC（按备份清单 + 校验 sha256）、回滚 3C 并移除部署的存档目录。确定继续？",
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                },
                buttons,
            }
        };

        var confirm = new Window
        {
            Title = "确认卸载活动存档",
            Width = 440, Height = 210,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = content,
        };

        // 事件绑定必须在 Show 之前
        cancelBtn.Click += (_, _) => confirm.Close();
        okBtn.Click += async (_, _) =>
        {
            confirm.Close();
            try
            {
                var stage = new System.Progress<string>(s =>
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => updateStatus.Text = s));
                var report = await deploy.UninstallAsync(stage);
                RefreshActive();
                updateStatus.Text = report.Ok ? "活动存档已卸载（DLC 已还原）" : "卸载失败：" + report.Error;
            }
            catch (Exception ex)
            {
                updateStatus.Text = "卸载异常：" + ex.Message;
            }
        };

        // 用 Show(owner) 而非 Owner 属性（Owner 只读）；owner 可能为 null（非窗口上下文）
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner != null)
            confirm.Show(owner);
        else
            confirm.Show();
    }
}

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

    private void OnPickSaveDir(object? sender, RoutedEventArgs e) => PickFolder(p => { _settings.SetSaveDir(p); RefreshDirs(); });
    private void OnPickGameDir(object? sender, RoutedEventArgs e) => PickFolder(p => { _settings.SetGameDir(p); RefreshDirs(); RefreshActive(); });
    private void OnPickGameDoc(object? sender, RoutedEventArgs e) => PickFolder(p => { _settings.SetGameDocDir(p); RefreshDirs(); });

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
            // Avalonia 11 IStorageFolder 不直接暴露 LocalPath（11.3 改为只暴露 Path 抽象）；
            // 用反射尝试常见属性，最后降级提示
            var item = folders[0];
            string? path = null;
            foreach (var name in new[] { "LocalPath", "Path", "DisplayPath", "PathString" })
            {
                try
                {
                    var p = item.GetType().GetProperty(name);
                    if (p?.GetValue(item) is string s) { path = s; break; }
                }
                catch { }
            }
            if (string.IsNullOrWhiteSpace(path))
                path = "(P0 降级：文件夹选择器 API 不可用，请手动在 .env 设置 SAVE_DIR/GAME_DIR)";
            Avalonia.Threading.Dispatcher.UIThread.Post(() => onPicked(path!));
        }
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

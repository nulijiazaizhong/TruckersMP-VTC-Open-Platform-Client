using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using VTC.Client.Services;
using VTC.Client.ViewModels;
using VTC.Shared;
using VTC.Shared.Dtos;

namespace VTC.Client.Views;

public partial class MainWindow : Window
{
    private ShellViewModel? _shell;
    private AuthService? _auth;
    private BrandingService? _branding;
    // 导航防抖：快速连点侧边栏会重复 new 页面（Profile/News 构造即 fire-and-forget 异步加载），
    // 堆积大量并发异步任务 + 内存膨胀 → UI 卡死。300ms 内的连点直接忽略。
    private bool _navigating;
    private int _lastNavTs;

    public MainWindow()
    {
        InitializeComponent();
        InitializeRuntime();
    }


    private void InitializeRuntime()
    {
        _auth = AppServiceLocator.Get<AuthService>();
        _branding = AppServiceLocator.Get<BrandingService>();
        _shell = new ShellViewModel(_auth, _branding);
        DataContext = _shell;

        _auth.AuthChanged += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            _shell?.RefreshAuthState();
            UpdateNavVisibility();
        });

        // 登录成功后切到首页
        _auth.AuthChanged += (_, _) =>
        {
            if (_auth.IsAuthenticated)
                Dispatcher.UIThread.Post(() => ShowPage("home"));
        };

        ApplyBrand(_branding.Current);
        UpdateNavVisibility();
    }

    private void ApplyBrand(PublicConfigDto cfg)
    {
        _shell.TeamName = cfg.TeamName;
        _shell.TeamSlogan = cfg.TeamSlogan;
        _ = LoadBrandImageAsync(cfg.TeamLogo, "pack://app-assets/brand/logo.png", isLogo: true, bmp =>
            Dispatcher.UIThread.Post(() => _shell.BrandLogo = bmp));
        var baseTitle = string.IsNullOrWhiteSpace(cfg.TeamName) ? "VTC 车队平台" : $"{cfg.TeamName} 车队平台";
        // 开发模式标识：避免开发/生产混淆，标题直接显示当前后端地址。
        Title = App.DevMode
            ? $"{baseTitle}  [DEV · {App.DevApiBase}]"
            : baseTitle;
    }

    private async Task LoadBrandImageAsync(string source, string fallback, bool isLogo, Action<Bitmap?> cb)
    {
        try
        {
            var bmp = await _branding!.ResolveBrandImageAsync(source, fallback, isLogo);
            cb(bmp);
        }
        catch { cb(null); }
    }

    public void NavigateToHome() => ShowPage("home");
    /// <summary>导航到指定页（home/news/settings/admin/profile）。开发模式 --navigate= 用它直达某页做诊断/截图。</summary>
    public void NavigateTo(string key) => ShowPage(key);

    // 页面实例复用：同页不重复 new（Profile/News 构造即 fire-and-forget 异步加载，
    // 反复 new 会堆积并发异步任务 + 内存膨胀 → 快速连点时 UI 卡死）。
    private readonly Dictionary<string, Control> _pageCache = new();

    private void ShowPage(string key)
    {
        _shell.ActiveNav = key;
        UpdateNavVisibility();
        UpdateNavActive(key);
        if (!_pageCache.TryGetValue(key, out var page))
        {
            page = CreatePage(key);
            _pageCache[key] = page;
        }
        contentHost.Content = page;
    }

    /// <summary>
    /// 同步导航按钮选中态：给 Tag==key 的按钮加 .active 类（XAML 的 .active 类选择器渲染强调色背景），
    /// 其余移除。此前没有任何代码切换 .active 类，导致选中态从未显示（截图里看到的"高亮"其实是 :pointerover hover）。
    /// </summary>
    private void UpdateNavActive(string key)
    {
        // 导航按钮无 x:Name，靠 Classes="nav-item" 标识；一次遍历视觉树收集，按 Tag 匹配选中态。
        var navTags = new HashSet<string> { "home", "news", "settings", "admin", "profile" };
        var navButtons = this.GetVisualDescendants()
            .OfType<Avalonia.Controls.Button>()
            .Where(b => b.Classes.Contains("nav-item") && navTags.Contains(b.Tag as string ?? ""))
            .ToList();
        foreach (var btn in navButtons)
        {
            bool isActive = string.Equals(btn.Tag as string, key, StringComparison.Ordinal);
            if (isActive) btn.Classes.Add("active");
            else btn.Classes.Remove("active");
        }
    }

    private Control CreatePage(string key)
    {
        switch (key)
        {
            case "news": return new NewsView();
            case "settings": return new SettingsView();
            case "admin": return new AdminView();
            case "profile": return new ProfileView();
            case "home":
            default:
                return new HomeView();
        }
    }

    private void UpdateNavVisibility()
    {
        // 管理入口仅 Admin/Manager 可见
        var isAdmin = _auth?.CurrentRole is Constants.RoleAdmin or Constants.RoleManager;
        _shell.IsAdmin = isAdmin;
        // adminNav 通过 x:Name 生成（如果 Avalonia 生成器支持）
        // 否则通过 FindControl 查找
        try
        {
            if (this.FindControl<Avalonia.Controls.Button>("adminNav") is { } btn)
                btn.IsVisible = isAdmin;
        }
        catch { }
    }

    // ===== 事件 =====

    private void OnNavClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag })
            ShowPage(tag);
    }

    private void OnLogout(object? sender, RoutedEventArgs e)
    {
        _auth?.Logout();
        // 登录层通过 IsAuthenticated=false 显示（XAML 绑定驱动）
    }

    private void OnMinimize(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaxRestore(object? sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

}

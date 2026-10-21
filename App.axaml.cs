using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Serilog.Extensions.Logging;
using VTC.Client.Services;
using VTC.Client.Views;

namespace VTC.Client;

/// <summary>
/// Avalonia 应用类。样式在 App.axaml（FluentTheme）。
/// 启动/服务初始化逻辑全部在 OnFrameworkInitializationCompleted。
/// </summary>
public class App : Application
{
    public static App Instance { get; private set; } = null!;
    public IConfiguration Config { get; private set; } = null!;

    // ===== 开发模式（规划 §2.4 衍生：开发默认连本地 docker 服务端）=====
    // 命令行 --dev 的三态：null=未显式指定（回退 VTC_DEV_MODE 环境变量），true/false=显式。
    public static bool? DevModeExplicit { get; set; }
    /// <summary>开发模式下 --api-base= 显式指定的后端地址（优先于本地 docker 默认值）。</summary>
    public static string? DevApiBaseOverride { get; set; }
    /// <summary>开发模式最终是否启用（Bootstrapper 解析后写回，UI 可读取显示标识）。</summary>
    public static bool DevMode { get; private set; }
    /// <summary>开发模式下生效的后端地址（UI 显示用；生产模式为 .env 配置的地址）。</summary>
    public static string? DevApiBase { get; private set; }
    /// <summary>本地 docker 服务端默认地址（容器 -p 8731:8731）。</summary>
    public const string DevDefaultApiBase = "http://127.0.0.1:8731";
    /// <summary>开发模式 --auto-login=user:pass（仅 DevMode 生效，启动后自动登录，便于 F5 验证）。</summary>
    public static string? DevAutoLogin { get; set; }
    /// <summary>开发模式 --navigate=key（仅 DevMode 生效，登录/启动后直达某页，便于诊断/截图）。</summary>
    public static string? DevNavigate { get; set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        Instance = this;
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            Bootstrapper(desktop);
        }
        base.OnFrameworkInitializationCompleted();
    }

    private void Bootstrapper(IClassicDesktopStyleApplicationLifetime desktop)
    {
        // 1) 配置
        Config = EnvLoader.BuildConfiguration(out _);

        // 1.5) 开发模式：默认指向本地 docker 服务端（规划 §2.4 衍生）
        // 优先级：命令行 --dev（显式）> 环境变量 VTC_DEV_MODE > 默认关。
        // 启用后：若用户未显式配置 VTC_API_BASE（.env/环境变量），覆盖为本地 docker 默认地址；
        //        若给了 --api-base= 则用该地址（docker 映射到非 8731 端口时）。
        ApplyDevMode();

        // 2) 日志（Serilog 文件 + 控制台）
        var logLevel = ParseLogLevel(EnvLoader.Get(Config, "LOG_LEVEL") ?? "info");
        Logging.Serilog = new LoggerConfiguration()
            .MinimumLevel.Override("VTC", logLevel)
            .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
            .WriteTo.File(Paths.LogFile, rollingInterval: RollingInterval.Day, retainedFileCountLimit: 5,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext} {Message:lj}{NewLine}{Exception}")
            .WriteTo.Console()
            .CreateLogger();

        // 3) DI（应用级单例）
        var services = new ServiceCollection();
        services.AddSingleton(Config);
        services.AddLogging(b => b.AddSerilog(Logging.Serilog, dispose: true));
        services.AddSingleton<SecureTokenStore>();
        services.AddSingleton<IApiClient, ApiClient>();
        services.AddSingleton<BrandingService>();
        // AuthService 同时按具体类型与接口解析（5 处用 Get<AuthService>，1 处用 IAuthService），
        // 且必须共享同一单例实例（token 状态不能分裂）：先注册具体类型单例，再转发接口。
        services.AddSingleton<AuthService>();
        services.AddSingleton<IAuthService>(sp => sp.GetRequiredService<AuthService>());
        services.AddSingleton<AppSettings>();
        services.AddSingleton<SaveDownloader>();
        services.AddSingleton<ThreeCSyncService>();
        services.AddSingleton<DlcPruneService>();
        services.AddSingleton<DeployService>();
        services.AddSingleton<UpdateService>();
        services.AddSingleton<UnreadService>();
        services.AddSingleton<PushClient>();
        AppServiceLocator.Initialize(services);

        var sp = AppServiceLocator.Services;
        var branding = sp.GetRequiredService<BrandingService>();
        var auth = sp.GetRequiredService<AuthService>();

        // 4) 拉品牌 → 恢复会话 → 显示主窗
        _ = StartupAsync(branding, auth, desktop);
    }

    /// <summary>
    /// 解析并应用开发模式（规划 §2.4 衍生）。
    /// 优先级：命令行 --dev（显式）> 环境变量 VTC_DEV_MODE > 默认关。
    /// 启用后覆盖 VTC_API_BASE：--api-base（若有）> 本地 docker 默认（127.0.0.1:8731），
    /// 但环境变量 VTC_API_BASE 仍最高（CI/部署显式指定时不被 dev 默认值覆盖）。
    /// </summary>
    private void ApplyDevMode()
    {
        // 1) 决定是否启用
        var envFlag = EnvLoader.Get(Config, "VTC_DEV_MODE");
        bool envOn = envFlag is "1" or "true" or "yes" or "on";
        bool on = DevModeExplicit ?? envOn;
        DevMode = on;

        if (!on) { DevApiBase = EnvLoader.Get(Config, "VTC_API_BASE"); return; }

        // 2) 开发模式：计算目标 API 地址
        // 环境变量 VTC_API_BASE 最高（用户显式指定，CI 注入），其次 --api-base，最后本地 docker 默认。
        var envApi = EnvLoader.Get(Config, "VTC_API_BASE");
        var target = (DevApiBaseOverride is { Length: > 0 } ? DevApiBaseOverride
            : envApi is { Length: > 0 } && !string.Equals(envApi, DevDefaultApiBase, StringComparison.OrdinalIgnoreCase)
                ? envApi                       // 环境变量显式给了非默认地址 → 尊重它
                : DevDefaultApiBase);          // 否则本地 docker 默认

        DevApiBase = target;

        // 仅当目标与当前配置不同时才重建 Config（覆盖 VTC_API_BASE），ApiClient 从 Config 读取。
        if (!string.Equals(envApi, target, StringComparison.OrdinalIgnoreCase))
        {
            Config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
                .AddConfiguration(Config)
                .AddInMemoryCollection(new Dictionary<string, string?> { ["VTC_API_BASE"] = target })
                .Build();
        }

        System.Diagnostics.Trace.WriteLine($"[VTC] 开发模式已启用，API 指向 {target}（默认本地 docker 服务端）");
    }

    private async Task StartupAsync(BrandingService branding, Services.AuthService auth, IClassicDesktopStyleApplicationLifetime desktop)
    {
        try
        {
            var cfg = await branding.LoadAsync();
            Paths.SetTeamSlug(string.IsNullOrWhiteSpace(cfg.TeamName) ? "default" : cfg.TeamName);
            ApplyBrandTokens(cfg);
            var restored = auth.RestoreSession();
            var main = new MainWindow();
            desktop.MainWindow = main;
            main.Show();
            if (restored != null) main.NavigateToHome();

            // 应用图标（.env VTC_APP_ICON）：运行时覆盖 Window.Icon（任务栏/标题栏）。
            // 支持本地路径 / pack:// / http(s)（http(s) 需 VTC_CDN_DOMAINS 白名单）。
            // 留空则保持 csproj ApplicationIcon 编译进的 .ico。异步加载，不阻塞窗口显示。
            _ = ApplyAppIconAsync(branding, main);

            // 开发模式 --auto-login=user:pass：免手输，启动后自动登录（验证完整流程 / F5 调试）。
            // 生产路径（非 DevMode）忽略该参数。
            if (DevMode && DevAutoLogin is { Length: > 0 } && !auth.IsAuthenticated)
            {
                var sep = DevAutoLogin.IndexOf(':');
                if (sep > 0)
                {
                    var u = DevAutoLogin[..sep];
                    var p = DevAutoLogin[(sep + 1)..];
                    _ = AutoLoginAsync(auth, main, u, p);
                }
            }
        }
        catch (Exception ex)
        {
            // 启动失败也要能显示窗口（否则进程空挂无 UI，难以诊断）。
            Logging.Serilog?.Error(ex, "启动失败：{Ex}", ex.Message);
            try
            {
                var main = new MainWindow();
                desktop.MainWindow = main;
                main.Show();
            }
            catch (Exception ex2)
            {
                Logging.Serilog?.Error(ex2, "回退 MainWindow 也失败：{Ex}", ex2.Message);
            }
        }
    }

    /// <summary>开发模式 --auto-login：启动后自动登录。成功后 AuthChanged 事件驱动 MainWindow 切到首页。</summary>
    private static async Task AutoLoginAsync(Services.AuthService auth, Views.MainWindow main, string user, string pass)
    {
        try
        {
            System.Diagnostics.Trace.WriteLine($"[VTC] 自动登录 {user} ...");
            var result = await auth.LoginAsync(user, pass, rememberMe: true);
            System.Diagnostics.Trace.WriteLine($"[VTC] 自动登录成功：{result?.User?.Username}（{result?.User?.Role}）");
            // 登录成功 → AuthService 触发 AuthChanged → MainWindow 处理器 ShowPage("home")。
            // 双保险：若事件未触发（时序），显式导航一次；--navigate 指定页则直达该页（诊断/截图）。
            if (auth.IsAuthenticated)
            {
                var page = DevNavigate is { Length: > 0 } ? DevNavigate : "home";
                Avalonia.Threading.Dispatcher.UIThread.Post(() => main.NavigateTo(page));
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine("[VTC] 自动登录失败: " + ex.Message);
            Logging.Serilog?.Error(ex, "自动登录失败：{Ex}", ex.Message);
        }
    }

    /// <summary>
    /// 应用 .env 的 VTC_APP_ICON 到 Window.Icon（任务栏/标题栏图标）。
    /// 支持本地文件 / pack:// / http(s)（http(s) 需 VTC_CDN_DOMAINS 白名单）。
    /// 留空或加载失败 → 保持 csproj ApplicationIcon 编译进的默认 .ico（不动 Window.Icon）。
    /// </summary>
    private static async Task ApplyAppIconAsync(BrandingService branding, Views.MainWindow main)
    {
        var src = EnvLoader.Get(branding.Cfg, "VTC_APP_ICON");
        if (string.IsNullOrWhiteSpace(src)) return;   // 未配置 → 用编译期 .ico
        try
        {
            // 复用品牌图加载器（pack/本地/http(s)+白名单/缓存/格式校验）；fallback 传空，失败则不设图标。
            var bmp = await branding.ResolveBrandImageAsync(src, "", isLogo: true);
            if (bmp != null)
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    // Avalonia 11 的 Window.Icon 是 WindowIcon 包装类型（非 Bitmap）
                    main.Icon = new Avalonia.Controls.WindowIcon(bmp);
                    System.Diagnostics.Trace.WriteLine($"[VTC] 应用图标已加载：{src}");
                });
            else
                System.Diagnostics.Trace.WriteLine($"[VTC] 应用图标加载失败，保持默认：{src}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine("[VTC] 应用图标加载异常： " + ex.Message);
            Logging.Serilog?.Error(ex, "应用图标加载异常：{Ex}", ex.Message);
        }
    }

    private static Serilog.Events.LogEventLevel ParseLogLevel(string s) => s.ToLowerInvariant() switch
    {
        "debug" => Serilog.Events.LogEventLevel.Verbose,
        "warning" => Serilog.Events.LogEventLevel.Warning,
        "error" => Serilog.Events.LogEventLevel.Error,
        "critical" => Serilog.Events.LogEventLevel.Fatal,
        "none" => Serilog.Events.LogEventLevel.Verbose,
        _ => Serilog.Events.LogEventLevel.Information,
    };

    /// <summary>品牌换肤：车队 ACCENT_COLOR 注入 Application 资源（§4.3）。</summary>
    public void ApplyBrandTokens(Shared.Dtos.PublicConfigDto cfg)
    {
        try
        {
            if (Avalonia.Application.Current != null)
            {
                if (!string.IsNullOrWhiteSpace(cfg.AccentColor) && TryParseColor(cfg.AccentColor, out var color))
                {
                    var accent = color;
                    var hover = new Color(
                        (byte)Math.Max(0, accent.R - 20),
                        (byte)Math.Max(0, accent.G - 20),
                        (byte)Math.Max(0, accent.B - 20),
                        255);
                    Avalonia.Application.Current.Resources["vtc_accent"] = accent;
                    Avalonia.Application.Current.Resources["vtc_accent_hover"] = hover;
                    Avalonia.Application.Current.Resources["AccentColor"] = accent;
                }
                if (!string.IsNullOrWhiteSpace(cfg.TeamName))
                    Avalonia.Application.Current.Resources["vtc_team_name"] = cfg.TeamName;
            }
        }
        catch (Exception ex)
        {
            Logging.Serilog?.Warning("应用品牌令牌失败：{Ex}", ex.Message);
        }
    }

    private static bool TryParseColor(string hex, out Color color)
    {
        color = default;
        try
        {
            var h = hex.StartsWith("#") ? hex : "#" + hex;
            if (h.Length != 7) return false;
            color = Color.FromUInt32(0xFF000000 | uint.Parse(h.Substring(1), System.Globalization.NumberStyles.HexNumber));
            return true;
        }
        catch { return false; }
    }
}

/// <summary>Serilog 静态桥。</summary>
public static class Logging
{
    public static Serilog.ILogger? Serilog;
}

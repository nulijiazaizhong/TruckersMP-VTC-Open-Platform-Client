using Avalonia;

namespace VTC.Client;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // 解析命令行参数（开发模式入口，规划 §2.4：开发模式默认连本地 docker 服务端）。
        // 支持：
        //   --dev / --dev=true      开启开发模式（默认 API 指向 127.0.0.1:8731 本地 docker）
        //   --dev=false             显式关闭（覆盖 VTC_DEV_MODE 环境变量）
        //   --api-base=<url>        开发模式下显式指定后端地址（如 docker 映射到别的端口）
        //   --data-dir=<path>       开发模式数据目录重定向（等价 VTC_DATA_DIR，便于调试隔离）
        foreach (var a in args)
        {
            if (string.IsNullOrWhiteSpace(a)) continue;
            var kv = a.StartsWith("--") ? a[2..] : a;
            var eq = kv.IndexOf('=');
            var key = (eq >= 0 ? kv[..eq] : kv).Trim().ToLowerInvariant();
            var val = eq >= 0 ? kv[(eq + 1)..] : null;

            switch (key)
            {
                case "dev":
                    // --dev / --dev=true → 开；--dev=false → 关（不写则保持 null=未显式指定）
                    App.DevModeExplicit = val is null or "true" or "1" ? true
                        : val is "false" or "0" ? false
                        : App.DevModeExplicit;
                    break;
                case "api-base":
                    if (val is not null) App.DevApiBaseOverride = val;
                    break;
                case "data-dir":
                    if (val is not null) Environment.SetEnvironmentVariable("VTC_DATA_DIR", val);
                    break;
                // --auto-login=user:pass  开发模式辅助：启动后自动登录（免手输，便于 F5 验证完整流程）。
                // 仅在 DevMode 启用时生效（生产路径忽略），格式 user:pass（密码含 ':' 时用 user::pass）。
                case "auto-login":
                    if (val is not null) App.DevAutoLogin = val;
                    break;
                // --navigate=home|news|settings|admin|profile  开发模式辅助：登录/启动后直达某页（诊断/截图）。
                case "navigate":
                    if (val is not null) App.DevNavigate = val;
                    break;
            }
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .LogToTrace();
}

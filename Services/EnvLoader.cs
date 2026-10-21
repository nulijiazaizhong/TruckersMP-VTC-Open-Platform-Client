using System.IO;
using Microsoft.Extensions.Configuration;

namespace VTC.Client.Services;

/// <summary>
/// 客户端 .env loader（规划 §2.4）。
/// 查找顺序：VTC_HOME 环境变量指定目录 → 可执行文件目录 → 可执行文件目录/.config。
/// 只接受"连接与展示"类配置；严禁任何后端 secret（DB/S3/JWT 等）。
/// 品牌字段（TEAM_*/LOGIN_*/ACCENT_COLOR）可被 /config/public 覆盖（由 BrandingService 负责）。
/// </summary>
public sealed class EnvLoader
{
    public static string? ExeDir { get; private set; }

    /// <summary>执行 .env 中的 %VAR% / $VAR 环境展开。</summary>
    public static string Expand(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;
        return System.Environment.ExpandEnvironmentVariables(value);
    }

    /// <summary>解析 .env 所在目录与文件路径。</summary>
    public static (string? dir, string? file) LocateEnvFile()
    {
        var exeDir = AppContext.BaseDirectory;
        ExeDir = exeDir;

        var candidates = new List<string>();
        var vtcHome = Environment.GetEnvironmentVariable("VTC_HOME");
        if (!string.IsNullOrWhiteSpace(vtcHome))
            candidates.Add(Path.Combine(vtcHome, ".env"));
        candidates.Add(Path.Combine(exeDir, ".env"));
        candidates.Add(Path.Combine(exeDir, ".config", ".env"));
        candidates.Add(Path.Combine(exeDir, "config", ".env"));

        foreach (var c in candidates)
        {
            if (File.Exists(c))
            {
                try { Directory.SetCurrentDirectory(Path.GetDirectoryName(c)!); } catch { /* ignore */ }
                return (Path.GetDirectoryName(c)!, c);
            }
        }
        return (exeDir, null);
    }

    /// <summary>
    /// 构建 IConfiguration：内置默认值 → .env 文件 → 环境变量（VTC_ 前缀，大写不敏感）。
    /// </summary>
    public static IConfiguration BuildConfiguration(out string? envFilePath)
    {
        var (dir, file) = LocateEnvFile();
        envFilePath = file;

        var builder = new ConfigurationBuilder()
            .SetBasePath(dir ?? AppContext.BaseDirectory)
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["VTC_API_BASE"] = "http://127.0.0.1:8731",
                ["VTC_WS_BASE"] = null,
                ["TEAM_NAME"] = "VTC 车队",
                ["TEAM_SLOGAN"] = "",
                ["TEAM_LOGO"] = "pack://app-assets/brand/logo.png",
                ["LOGIN_BG"] = "pack://app-assets/brand/login_bg.jpg",
                ["LOGIN_LAYOUT"] = "left",
                // 应用图标（运行时窗口/任务栏图标，Window.Icon）。支持本地路径 / pack:// / http(s)。
                // 留空 = 用 csproj 的 ApplicationIcon（编译期 .ico，exe 文件图标）。
                // http(s) 源需 VTC_CDN_DOMAINS 白名单（见下）。
                ["VTC_APP_ICON"] = null,
                // 品牌图/图标允许远程下载的 CDN 域名白名单（逗号分隔）。
                // 例：api.goodnightan.com,cdn.example.com —— TEAM_LOGO/LOGIN_BG/VTC_APP_ICON 用 http(s) 时，
                // 其 host 必须命中此白名单或 VTC_API_BASE 同主机，否则回退本地兜底。
                ["VTC_CDN_DOMAINS"] = null,
                ["ACCENT_COLOR"] = "#1E88E5",
                ["AUTH_MODE"] = "hybrid",
                ["STEAM_API_KEY"] = null,
                ["TMP_SERVER_ID"] = null,
                ["SAVE_DIR"] = @"%USERPROFILE%\Documents\Euro Truck Simulator 2\mods\SaveData",
                ["ALLOW_UPLOAD"] = "true",
                ["UPDATE_CHANNEL"] = "stable",
                ["LOG_LEVEL"] = "info",
                ["GAME_DIR"] = null,
            });

        if (file != null)
            builder.AddJsonFile(Path.GetFileName(file), optional: false, reloadOnChange: true);

        var baseCfg = builder.Build();

        // 环境变量覆盖（便于 CI / 部署注入）：VTC_X → 配置键 X
        var envOverrides = new Dictionary<string, string?>();
        foreach (System.Collections.DictionaryEntry kv in System.Environment.GetEnvironmentVariables())
        {
            var k = kv.Key as string ?? "";
            if (k.StartsWith("VTC_", StringComparison.OrdinalIgnoreCase) && k.Length > 4)
                envOverrides[k.Substring(4)] = kv.Value as string;
        }
        var finalBuilder = new ConfigurationBuilder()
            .AddConfiguration(baseCfg)
            .AddInMemoryCollection(envOverrides);
        return finalBuilder.Build();
    }

    /// <summary>读取单个配置值（已做环境变量展开，未找到返回 null）。</summary>
    public static string? Get(IConfiguration cfg, string key)
    {
        var v = cfg[key];
        if (string.IsNullOrWhiteSpace(v)) return null;
        return Expand(v);
    }
}

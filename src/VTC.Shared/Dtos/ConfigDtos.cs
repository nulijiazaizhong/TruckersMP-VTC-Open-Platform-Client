namespace VTC.Shared.Dtos;

/// <summary>
/// 品牌与功能开关（GET /api/v1/config/public，客户端启动第一调用）。
/// 字段与 p0-contract 第 2 节完全一致（camelCase 序列化）。
/// </summary>
public class PublicConfigDto
{
    public string TeamName { get; set; } = "";
    public string TeamSlogan { get; set; } = "";
    /// <summary>pack:// 或 https://</summary>
    public string TeamLogo { get; set; } = "";
    /// <summary>pack:// 或 https://</summary>
    public string LoginBg { get; set; } = "";
    /// <summary>left | right | center</summary>
    public string LoginLayout { get; set; } = "left";
    /// <summary>#RRGGBB</summary>
    public string AccentColor { get; set; } = "#1E88E5";
    public string DefaultLang { get; set; } = "zh-CN";
    public FeaturesDto Features { get; set; } = new();
    public string UpdateChannel { get; set; } = "stable";
}

/// <summary>功能开关（车队可配，默认值见常量）。</summary>
public class FeaturesDto
{
    public bool AllowUpload { get; set; } = true;
    public bool SteamBinding { get; set; }
    public bool TmpBinding { get; set; } = true;
    public bool RssEnabled { get; set; } = true;
}

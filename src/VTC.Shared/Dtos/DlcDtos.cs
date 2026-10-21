namespace VTC.Shared.Dtos;

/// <summary>
/// 地图类文件名模板（GET /api/v1/dlc/map-template?game=&major=）。
/// 每行一个真实 .scs 文件名（整名匹配，勿用前缀）。
/// </summary>
public class DlcMapTemplateDto
{
    public string Game { get; set; } = "";
    public string Major { get; set; } = "";
    public bool Found { get; set; }
    public List<string> Files { get; set; } = [];
    /// <summary>模板文件路径（调试用）。</summary>
    public string? TemplatePath { get; set; }
}

/// <summary>车队要求的 DLC 条目（GET /api/v1/dlc/required）。</summary>
public class DlcRequiredItemDto
{
    public string DlcId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public bool IsMap { get; set; }
    public bool Required { get; set; }
    public string? PurchaseUrl { get; set; }
}

/// <summary>车队要求 DLC 列表响应。</summary>
public class DlcRequiredDto
{
    public string Game { get; set; } = "";
    public string Version { get; set; } = "";
    public List<DlcRequiredItemDto> Items { get; set; } = [];
}

/// <summary>本地 DLC 校验上报（POST /api/v1/dlc/report）。</summary>
public class DlcReportDto
{
    public string Game { get; set; } = Constants.GameETS2;
    public List<string> Missing { get; set; } = [];
    public List<string> Installed { get; set; } = [];
}

/// <summary>DLC 上报响应。</summary>
public class DlcReportResultDto
{
    public bool Accepted { get; set; }
    public int MissingCount { get; set; }
}

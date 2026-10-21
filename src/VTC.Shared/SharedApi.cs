namespace VTC.Shared;

/// <summary>API 契约常量（客户端/服务端共用，见 p0-contract.md §1/§2）。</summary>
public static class VtcApi
{
    public const string Prefix = "/api/v1";

    public const string ConfigPublic = Prefix + "/config/public";
    public const string AuthLogin = Prefix + "/auth/login";
    public const string AuthRefresh = Prefix + "/auth/refresh";
    public const string Announcements = Prefix + "/announcements";
    public const string Events = Prefix + "/events";
    public const string EventDetail = Prefix + "/events/{id}";
    public const string Saves = Prefix + "/saves";
    public const string SaveDownloadCredential = Prefix + "/saves/{id}/download-credential";
    public const string DlcMapTemplate = Prefix + "/dlc/map-template";
    public const string DlcRequired = Prefix + "/dlc/required";
    public const string DlcReport = Prefix + "/dlc/report";
    public const string Updates = "/updates";
}

/// <summary>本地路径约定（p0-contract.md §7）。</summary>
public static class LocalPaths
{
    public const string Ets2GameDoc = @"%USERPROFILE%\Documents\Euro Truck Simulator 2";
    public const string Ets2DefaultSaveDir = @"%USERPROFILE%\Documents\Euro Truck Simulator 2\mods\SaveData";
    /// <summary>profile 存档内的 3C 子目录名（按存在性检测，不硬编码假设）。</summary>
    public const string ConfigDir = "455453324C41";
    /// <summary>3C 三文件（§3.6）。</summary>
    public static readonly string[] ThreeCFiles = ["config.cfg", "config_local.cfg", "controls.sii"];
}

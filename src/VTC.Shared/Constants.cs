namespace VTC.Shared;

/// <summary>
/// 平台级常量：角色、游戏、状态、端点错误码。
/// 客户端与服务端共享，保证两端对同一字符串语义一致。
/// </summary>
public static class Constants
{
    // ---- 角色（RBAC，从高到低）----
    public const string RoleAdmin = "Admin";
    public const string RoleManager = "Manager";
    public const string RoleDriver = "Driver";
    public const string RoleMember = "Member";

    /// <summary>管理端允许的最低角色（Admin/Manager 可走 /admin 类管理端点）。</summary>
    public static readonly string[] RolesAdminOrManager = { RoleAdmin, RoleManager };

    // ---- 游戏 ----
    public const string GameETS2 = "ets2";
    public const string GameATS = "ats";

    // ---- 成员 / 用户状态 ----
    public const string StatusActive = "Active";
    public const string StatusDisabled = "Disabled";
    public const string StatusLeft = "Left";

    // ---- 公告 / 活动状态 ----
    public const string StatusDraft = "Draft";
    public const string StatusPublished = "Published";
    public const string StatusArchived = "Archived";
    public const string StatusActiveEvt = "active";
    public const string StatusScheduledEvt = "scheduled";
    public const string StatusEndedEvt = "ended";
    public const string StatusCancelledEvt = "cancelled";

    // ---- 功能开关默认值（来自 /config/public 的 features）----
    public const string FeatureAllowUpload = "allowUpload";
    public const string FeatureSteamBinding = "steamBinding";
    public const string FeatureTmpBinding = "tmpBinding";
    public const string FeatureRssEnabled = "rssEnabled";

    // ---- JWT ----
    public const string JwtIssuer = "VTC";
    public const string JwtAudience = "VTC";

    /// <summary>Access token 寿命（秒）：2 小时。</summary>
    public const int AccessTokenSeconds = 7200;

    /// <summary>Refresh token 寿命（秒）：7 天。</summary>
    public const int RefreshTokenSeconds = 7 * 24 * 3600;

    /// <summary>预签名直传/下载 URL 寿命（秒）：1 小时。</summary>
    public const int PresignedSeconds = 3600;

    // ---- 审计动作 ----
    public const string AuditLogin = "auth.login";
    public const string AuditLoginFail = "auth.login.fail";
    public const string AuditLogout = "auth.logout";
    public const string AuditRefresh = "auth.refresh";
    public const string AuditPublishSave = "save.publish";
    public const string AuditUnpublishSave = "save.unpublish";
    public const string AuditUploadCredential = "save.upload-credential";
    public const string AuditDeleteSave = "save.delete";
    public const string AuditMemberCreate = "member.create";
    public const string AuditMemberUpdate = "member.update";
    public const string AuditMemberDelete = "member.delete";
    public const string AuditAnnouncementCreate = "announcement.create";
    public const string AuditAnnouncementUpdate = "announcement.update";
    public const string AuditAnnouncementDelete = "announcement.delete";
    public const string AuditEventCreate = "event.create";
    public const string AuditEventUpdate = "event.update";
    public const string AuditEventDelete = "event.delete";
    public const string AuditDlcDisable = "dlc.disable";
    public const string AuditDlcRestore = "dlc.restore";
}

/// <summary>
/// 统一错误码（与 p0-contract 第 1 节 "错误响应" 对齐）。
/// </summary>
public static class ErrorCodes
{
    public const int Ok = 0;
    public const int BadRequest = 400;
    public const int Unauthorized = 401;
    public const int Forbidden = 403;
    public const int NotFound = 404;
    public const int Conflict = 409;
    public const int TooManyRequests = 429;
    public const int InternalError = 500;

    /// <summary>业务错误码（>1000，避免与 HTTP 混淆，前端可按此细分）。</summary>
    public const int BizUserNotFound = 1001;
    public const int BizBadCredentials = 1002;
    public const int BizUserDisabled = 1003;
    public const int BizRefreshInvalid = 1004;
    public const int BizRateLimited = 1005;
    public const int BizNotFound = 1006;
    public const int BizConflict = 1007;
    public const int BizInvalidVersion = 1008;
}

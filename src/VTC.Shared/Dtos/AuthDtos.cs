namespace VTC.Shared.Dtos;

/// <summary>登录请求（GET /api/v1/auth/login 按契约也接受 POST 语义的查询，字段与 p0-contract 第 2 节一致）。</summary>
public class AuthLoginDto
{
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public bool RememberMe { get; set; }

    /// <summary>可选：TM:MP 玩家 ID（身份绑定校验，P1 完整启用）。</summary>
    public string? TmpPlayerId { get; set; }

    /// <summary>可选：Steam ID（身份绑定校验，P1 完整启用）。</summary>
    public string? SteamId { get; set; }
}

/// <summary>登录/刷新响应中的用户信息。</summary>
public class AuthUserDto
{
    public Guid Id { get; set; }
    public string Username { get; set; } = "";
    public string Role { get; set; } = "";
    public string Status { get; set; } = "";
}

/// <summary>登录/刷新响应中的成员信息（用户可能无对应成员档案时为 null）。</summary>
public class AuthMemberDto
{
    public string? Plate { get; set; }
    public string? Vehicle { get; set; }
    public DateTime JoinedAt { get; set; }
    public string Status { get; set; } = "";
}

/// <summary>登录 / 刷新统一响应（member 可空）。</summary>
public class AuthLoginResultDto
{
    public string AccessToken { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    /// <summary>Access token 剩余寿命（秒）。</summary>
    public int ExpiresIn { get; set; } = Constants.AccessTokenSeconds;
    public AuthUserDto? User { get; set; }
    public AuthMemberDto? Member { get; set; }
}

/// <summary>刷新请求。</summary>
public class AuthRefreshDto
{
    public string RefreshToken { get; set; } = "";
}

namespace VTC.Shared.Dtos;

/// <summary>成员列表/详情（GET /api/v1/members、GET /api/v1/members/{id}）。</summary>
public class MemberDto
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Username { get; set; } = "";
    public string Role { get; set; } = "";
    public string Plate { get; set; } = "";
    public string Vehicle { get; set; } = "";
    public int Level { get; set; }
    public int Points { get; set; }
    public DateTime JoinedAt { get; set; }
    public string Status { get; set; } = "";
    public string? Note { get; set; }
}

/// <summary>创建成员（可选同时创建初始登录账号）。</summary>
public class MemberCreateDto
{
    public string Username { get; set; } = "";

    /// <summary>不填则生成随机初始密码并随响应返回（仅创建时可见）。</summary>
    public string? Password { get; set; }

    public string Role { get; set; } = Constants.RoleDriver;
    public string Status { get; set; } = Constants.StatusActive;
    public string Plate { get; set; } = "";
    public string Vehicle { get; set; } = "";
    public string? Note { get; set; }

    /// <summary>可选：绑定 TM:MP 玩家 ID（身份绑定，P1）。</summary>
    public string? TmpPlayerId { get; set; }
}

/// <summary>创建成员响应（含初始密码，仅在自动生成时返回）。</summary>
public class MemberCreatedDto
{
    public MemberDto Member { get; set; } = new();
    /// <summary>自动生成的初始密码（未显式提供 password 时）。</summary>
    public string? InitialPassword { get; set; }
}

/// <summary>PATCH /api/v1/members/{id}（字段可空 = 不修改）。</summary>
public class MemberUpdateDto
{
    public string? Role { get; set; }
    public string? Status { get; set; }
    public string? Plate { get; set; }
    public string? Vehicle { get; set; }
    public int? Level { get; set; }
    public int? Points { get; set; }
    public string? Note { get; set; }
}

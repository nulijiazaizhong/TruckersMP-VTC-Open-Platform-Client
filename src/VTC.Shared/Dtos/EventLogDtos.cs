namespace VTC.Shared.Dtos;

/// <summary>活动记录（C7：个人参赛/跑线/交付记录，时间线视图）。</summary>
public class EventLogDto
{
    public Guid Id { get; set; }
    public Guid MemberId { get; set; }
    public Guid EventId { get; set; }
    /// <summary>活动名称（服务端 JOIN 后填充）。</summary>
    public string? EventName { get; set; }
    /// <summary>completed / cancelled / incomplete。</summary>
    public string Result { get; set; } = "";
    /// <summary>跑线里程（km）。</summary>
    public long? Mileage { get; set; }
    /// <summary>收入（游戏货币）。</summary>
    public long? Income { get; set; }
    public DateTime At { get; set; }
}

/// <summary>创建活动记录（POST /api/v1/event-logs）。</summary>
public class EventLogCreateDto
{
    public Guid EventId { get; set; }
    public string Result { get; set; } = "completed";
    public long? Mileage { get; set; }
    public long? Income { get; set; }
}

/// <summary>排行榜条目（C11）。</summary>
public class LeaderboardEntryDto
{
    public long Rank { get; set; }
    public Guid MemberId { get; set; }
    public string Username { get; set; } = "";
    public string? Plate { get; set; }
    public long Points { get; set; }
}

/// <summary>排行榜响应（GET /api/v1/leaderboard）。</summary>
public class LeaderboardDto
{
    public List<LeaderboardEntryDto> Items { get; set; } = [];
    public long Total { get; set; }
}

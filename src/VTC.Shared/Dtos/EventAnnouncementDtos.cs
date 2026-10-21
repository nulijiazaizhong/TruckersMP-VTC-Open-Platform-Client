namespace VTC.Shared.Dtos;

/// <summary>公告（GET /api/v1/announcements）。</summary>
public class AnnouncementDto
{
    public Guid Id { get; set; }
    public string Title { get; set; } = "";
    /// <summary>富文本正文（HTML，客户端渲染前做白名单净化）。</summary>
    public string Body { get; set; } = "";
    public string Status { get; set; } = "";
    public DateTime? PublishedAt { get; set; }
    public DateTime? ScheduledAt { get; set; }
    public bool Pinned { get; set; }
    public Guid? AuthorId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>创建/发布公告（POST /api/v1/announcements）。</summary>
public class AnnouncementCreateDto
{
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public bool Pinned { get; set; }
    /// <summary>true=立即发布，false=存草稿（Draft）。</summary>
    public bool Publish { get; set; } = true;
    /// <summary>可选定时发布时间。</summary>
    public DateTime? ScheduledAt { get; set; }
}

/// <summary>更新公告（PATCH /api/v1/announcements/{id}，字段可空=不改）。</summary>
public class AnnouncementUpdateDto
{
    public string? Title { get; set; }
    public string? Body { get; set; }
    public bool? Pinned { get; set; }
    /// <summary>published / draft / archived。</summary>
    public string? Status { get; set; }
    public DateTime? ScheduledAt { get; set; }
}

/// <summary>活动（GET /api/v1/events、GET /api/v1/events/{id}）。</summary>
public class EventDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public DateTime ActivityAt { get; set; }
    public DateTime? DepartAt { get; set; }
    public string Server { get; set; } = "";
    /// <summary>导航方式（如"跟随头车"）。</summary>
    public string Navigation { get; set; } = "";
    public string? ImageKey { get; set; }
    public Guid? SaveId { get; set; }
    public string Status { get; set; } = "";
    /// <summary>活动确实用了某地图 DLC 时填真实文件名，部署时豁免不禁；默认 false=禁全部地图类。</summary>
    public string? RequiresMapDlc { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>创建/更新活动（POST /api/v1/events、PATCH .../{id}）。</summary>
public class EventCreateDto
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public DateTime ActivityAt { get; set; }
    public DateTime? DepartAt { get; set; }
    public string Server { get; set; } = "";
    public string Navigation { get; set; } = "";
    public string? ImageKey { get; set; }
    public Guid? SaveId { get; set; }
    public string Status { get; set; } = Constants.StatusActiveEvt;
    public string? RequiresMapDlc { get; set; }
}

/// <summary>更新活动（PATCH .../{id}，字段可空=不改）。</summary>
public class EventUpdateDto
{
    public string? Name { get; set; }
    public string? Type { get; set; }
    public DateTime? ActivityAt { get; set; }
    public DateTime? DepartAt { get; set; }
    public string? Server { get; set; }
    public string? Navigation { get; set; }
    public string? ImageKey { get; set; }
    public Guid? SaveId { get; set; }
    public string? Status { get; set; }
    public string? RequiresMapDlc { get; set; }
}

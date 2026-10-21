namespace VTC.Shared.Dtos;

/// <summary>
/// 分页结果包装（p0-contract 第 5 节）：items / total / page / pageSize。
/// </summary>
public class PageResult<T>
{
    public List<T> Items { get; set; } = [];
    public int Total { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;
}

/// <summary>统一错误响应（p0-contract 第 1 节：code/message/details）。</summary>
public class ApiError
{
    public int Code { get; set; }
    public string Message { get; set; } = "";
    public object? Details { get; set; }
}

/// <summary>通用分页查询参数（服务端从查询串解析，客户端可复用）。</summary>
public class PagedQuery
{
    public int Take { get; set; } = 20;
    public int Skip { get; set; }
}

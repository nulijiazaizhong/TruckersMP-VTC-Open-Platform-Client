using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace VTC.Client.Services;

/// <summary>
/// 未读标记（C8）。本地记录已读 id 集合（Paths.ReadIdsFile）。
/// 公告/活动 id 不在集合内 = 未读。
/// </summary>
public sealed class UnreadService
{
    private readonly ILogger<UnreadService> _log;
    private HashSet<Guid> _read = new();
    private readonly object _gate = new();

    public UnreadService(ILogger<UnreadService> log)
    {
        _log = log;
        Load();
    }

    private void Load()
    {
        try
        {
            if (File.Exists(Paths.ReadIdsFile))
            {
                var list = JsonSerializer.Deserialize<List<Guid>>(File.ReadAllText(Paths.ReadIdsFile));
                if (list != null) _read = new HashSet<Guid>(list);
            }
        }
        catch (Exception ex) { _log.LogWarning("读取 read_ids.json 失败：{Ex}", ex.Message); }
    }

    public bool IsRead(Guid id)
    {
        lock (_gate) return _read.Contains(id);
    }

    public int UnreadCount(IEnumerable<Guid> ids)
    {
        lock (_gate) return ids.Count(id => !_read.Contains(id));
    }

    public void MarkRead(Guid id)
    {
        bool added;
        lock (_gate) { added = _read.Add(id); }
        if (added) Save();
    }

    public void MarkAllRead(IEnumerable<Guid> ids)
    {
        bool any = false;
        lock (_gate) { foreach (var id in ids) any |= _read.Add(id); }
        if (any) Save();
    }

    private void Save()
    {
        try
        {
            FileUtil.EnsureDir(Paths.TeamDataDir);
            Guid[] arr;
            lock (_gate) arr = _read.ToArray();
            File.WriteAllText(Paths.ReadIdsFile, JsonSerializer.Serialize(arr));
        }
        catch (Exception ex) { _log.LogWarning("写 read_ids.json 失败：{Ex}", ex.Message); }
    }
}

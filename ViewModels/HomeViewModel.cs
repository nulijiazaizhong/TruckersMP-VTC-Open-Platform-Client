using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VTC.Client.Services;
using VTC.Shared;
using VTC.Shared.Dtos;

namespace VTC.Client.ViewModels;

/// <summary>首页视图模型：活动卡片流 + 公告列表 + 更新提示 + 一键部署。</summary>
public partial class HomeViewModel : ObservableObject
{
    private readonly IApiClient _api;
    private readonly AuthService _auth;
    private readonly DeployService _deploy;
    private readonly UnreadService _unread;
    private readonly UpdateService _update;
    private readonly AppSettings _settings;

    [ObservableProperty] private List<EventCard> _events = [];
    [ObservableProperty] private List<AnnouncementCard> _announcements = [];
    [ObservableProperty] private bool _isEmpty;
    [ObservableProperty] private bool _showUpdatePrompt;
    [ObservableProperty] private string _updateText = "";

    /// <summary>部署进度对话框状态。</summary>
    [ObservableProperty] private bool _isDeploying;
    [ObservableProperty] private string _deployStatus = "";
    [ObservableProperty] private double _deployProgress;
    [ObservableProperty] private string _deployLog = "";

    public HomeViewModel(IApiClient api, AuthService auth, DeployService deploy,
        UnreadService unread, UpdateService update, AppSettings settings)
    {
        _api = api; _auth = auth; _deploy = deploy; _unread = unread;
        _update = update; _settings = settings;
    }

    private static Dictionary<string, string?> Q(params (string K, string V)[] pairs)
    {
        var d = new Dictionary<string, string?>();
        foreach (var (k, v) in pairs) d[k] = v;
        return d;
    }

    public async Task LoadAsync()
    {
        var game = "ets2";
        var version = "1.61";

        // 活动（首页卡片流）
        try
        {
            var evts = await _api.GetAsync<PageResult<EventDto>>(VtcApi.Events,
                Q(("status", "active"), ("take", "20"), ("skip", "0")));
            Events = (evts?.Items ?? []).Select(e => new EventCard(e)).ToList();
        }
        catch (Exception ex) { Events = []; AppendLog("活动加载失败：" + ex.Message); }

        // 公告（列表 + 未读）
        try
        {
            var annos = await _api.GetAsync<PageResult<AnnouncementDto>>(VtcApi.Announcements,
                Q(("status", "published"), ("take", "20"), ("skip", "0")));
            Announcements = (annos?.Items ?? [])
                .Select(a => new AnnouncementCard(a, _unread.IsRead(a.Id)))
                .ToList();
        }
        catch (Exception ex) { Announcements = []; }

        IsEmpty = Events.Count == 0 && Announcements.Count == 0;
        _ = CheckUpdateAsync();
    }

    private async Task CheckUpdateAsync()
    {
        try
        {
            var info = await _update.CheckAsync();
            if (info.UpdateAvailable)
            {
                ShowUpdatePrompt = true;
                UpdateText = $"发现新版本 {info.LatestVersion}（当前 {_update.CurrentVersion}）" +
                             (string.IsNullOrWhiteSpace(info.Notes) ? "" : "：" + info.Notes);
            }
        }
        catch { /* 更新检查失败不打扰 */ }
    }

    [RelayCommand]
    private void DismissUpdate() => ShowUpdatePrompt = false;

    [RelayCommand]
    private async Task DownloadUpdateAsync()
    {
        // P0：提示 + 记录，实际替换由启动器执行
        AppendLog("开始下载更新包…（P0 预留：实际替换由外部启动器完成）");
    }

    /// <summary>一键部署活动存档（C1 核心交互）。</summary>
    [RelayCommand]
    public async Task DeployAsync(EventCard? card)
    {
        if (card == null) return;
        if (_settings.SaveDir == null || _settings.GameDir == null)
        {
            DeployStatus = "请先在设置页选择'存档目录'与'游戏目录'";
            IsDeploying = true;
            return;
        }

        IsDeploying = true;
        DeployLog = "";
        try
        {
            var progress = new Progress<DownloadProgress>(p =>
            {
                DeployProgress = p.Total > 0 ? p.Percent : 0;
                DeployStatus = p.Verifying ? "校验 sha256…" :
                    $"下载 {p.Received / 1048576.0:F0}MB / {p.Total / 1048576.0:F0}MB";
            });

            AppendLog($"① 拉取活动「{card.Name}」关联存档直传凭证…");
            var report = await _deploy.DeployAsync(card.Dto, "ets2", "1.61", progress);

            if (!report.Ok)
            {
                DeployStatus = "部署失败：" + report.Error;
                AppendLog("✗ " + report.Error);
                return;
            }

            AppendLog($"② 存档已部署 → {report.DeployedPath}");
            if (report.ThreeCCopied.Count > 0)
                AppendLog($"③ 3C 同步：从「{report.SourceProfile}」复制 {string.Join(", ", report.ThreeCCopied)}");
            if (report.DlcMoved.Count > 0)
                AppendLog($"④ DLC 裁剪：move {string.Join(", ", report.DlcMoved)}" +
                          (report.DlcExempted.Count > 0 ? $"；豁免 {string.Join(",", report.DlcExempted)}" : "") +
                          $"；保留车辆/涂装等 {report.DlcKept.Count} 个");
            DeployStatus = "部署完成 ✓";
            DeployProgress = 100;
            AppendLog("✓ 一键部署完成，可启动游戏进入活动");
        }
        catch (Exception ex)
        {
            DeployStatus = "部署异常：" + ex.Message;
            AppendLog("✗ " + ex.Message);
        }
        finally
        {
            // 稍后关闭进度（让最后一条状态可见）
            await Task.Delay(1500);
            IsDeploying = false;
        }
    }

    private void AppendLog(string line)
    {
        DeployLog = string.IsNullOrEmpty(DeployLog) ? line : DeployLog + "\n" + line;
    }
}

/// <summary>活动卡片（展示模型）。</summary>
public partial class EventCard : ObservableObject
{
    public EventDto Dto { get; }
    public EventCard(EventDto dto)
    {
        Dto = dto;
        Name = dto.Name;
        ActivityAtText = dto.ActivityAt.ToString("yyyy-MM-dd HH:mm");
        DepartAtText = dto.DepartAt?.ToString("HH:mm") ?? "-";
        Server = dto.Server;
        Navigation = dto.Navigation;
        StatusText = dto.Status;
        HasRequiresMapDlc = !string.IsNullOrWhiteSpace(dto.RequiresMapDlc);
        RequiresMapDlcText = HasRequiresMapDlc ? $"本活动使用地图 DLC（豁免不禁）：{dto.RequiresMapDlc}" : "";
    }
    public string Name { get; init; } = "";
    public string ActivityAtText { get; init; } = "";
    public string DepartAtText { get; init; } = "";
    public string Server { get; init; } = "";
    public string Navigation { get; init; } = "";
    public string StatusText { get; init; } = "";
    public bool HasRequiresMapDlc { get; init; }
    public string RequiresMapDlcText { get; init; } = "";
}

/// <summary>公告卡片（展示模型，含未读）。</summary>
public partial class AnnouncementCard : ObservableObject
{
    public AnnouncementDto Dto { get; }
    public bool IsUnread { get; }
    public string Title { get; init; } = "";
    public string PublishedAtText { get; init; } = "";
    public Brush UnreadDot { get; init; } = new SolidColorBrush(Color.FromRgb(204, 204, 204));
    public Brush TitleBrush { get; init; } = new SolidColorBrush(Color.FromRgb(34, 34, 34));

    public AnnouncementCard(AnnouncementDto dto, bool unread)
    {
        Dto = dto;
        IsUnread = unread;
        Title = dto.Title;
        PublishedAtText = dto.PublishedAt?.ToString("yyyy-MM-dd") ?? "";
        if (unread)
        {
            UnreadDot = new SolidColorBrush(Color.FromRgb(229, 57, 53));
            TitleBrush = new SolidColorBrush(Color.FromRgb(17, 17, 17));
        }
    }
}

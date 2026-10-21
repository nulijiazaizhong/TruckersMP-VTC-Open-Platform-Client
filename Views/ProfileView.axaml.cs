using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using VTC.Client.Services;
using VTC.Shared;
using VTC.Shared.Dtos;

namespace VTC.Client.Views;

/// <summary>
/// 个人中心（C7 活动记录 + C11 积分/排名，P1 实装）。
/// </summary>
public partial class ProfileView : UserControl
{
    private IApiClient? _api;
    private AuthService? _auth;

    public ProfileView()
    {
        InitializeComponent();
        var sp = AppServiceLocator.Services;
        _api = sp?.GetService<IApiClient>();
        _auth = sp?.GetService<AuthService>();
        var settings = sp?.GetService<AppSettings>();

        fUsername.Text = _auth?.CurrentUsername ?? "—";
        fRole.Text = _auth?.CurrentRole ?? "—";
        fPlate.Text = string.IsNullOrWhiteSpace(_auth?.Plate) ? "—" : _auth!.Plate;
        fVehicle.Text = string.IsNullOrWhiteSpace(_auth?.Vehicle) ? "—" : _auth!.Vehicle;
        fStatus.Text = _auth?.CurrentStatus ?? "—";

        activeText.Text = string.IsNullOrWhiteSpace(settings?.ActiveEventProfile)
            ? "（无已部署活动存档）"
            : $"profile：{settings.ActiveEventProfile}\nDLC 备份：{settings.ActiveDlcStashDir ?? "—"}";

        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        if (_api == null) return;

        // ① 活动记录（C7）
        try
        {
            var logs = await _api.GetAsync<PageResult<EventLogDto>>(VtcApi.EventLogs,
                new Dictionary<string, string?> { ["take"] = "20", ["skip"] = "0" });
            if (logs?.Items is { Count: > 0 })
            {
                var items = new ObservableCollection<EventLogItem>();
                foreach (var l in logs.Items)
                {
                    items.Add(new EventLogItem
                    {
                        Title = l.EventName ?? $"活动 {l.EventId:R}",
                        Detail = BuildDetail(l),
                        TimeText = l.At.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
                    });
                }
                records.ItemsSource = items;
                recordsEmpty.IsVisible = false;
            }
        }
        catch { recordsEmpty.Text = "活动记录加载失败"; }

        // ② 排行榜（C11）
        try
        {
            var lb = await _api.GetAsync<LeaderboardDto>(VtcApi.Leaderboard,
                new Dictionary<string, string?> { ["take"] = "10", ["skip"] = "0" }, useAuth: false);
            if (lb?.Items is { Count: > 0 })
            {
                var entries = new ObservableCollection<LbEntry>();
                var myRank = 0;
                foreach (var e in lb.Items)
                {
                    // 尝试匹配当前用户
                    var username = _auth?.CurrentUsername;
                    if (!string.IsNullOrWhiteSpace(username)
                        && e.Username.Equals(username, StringComparison.OrdinalIgnoreCase))
                        myRank = (int)e.Rank;
                    entries.Add(new LbEntry
                    {
                        RankText = e.Rank <= 3 ? $"#{e.Rank} 🏅" : $"#{e.Rank}",
                        RankBrush = e.Rank <= 3
                            ? new SolidColorBrush(Color.FromRgb(255, 193, 7))
                            : new SolidColorBrush(Color.FromRgb(136, 136, 136)),
                        Username = e.Username,
                        Plate = e.Plate ?? "",
                        PointsText = $"{e.Points} 分",
                    });
                }
                leaderboard.ItemsSource = entries;
                lbEmpty.IsVisible = false;

                // 积分与排名文本
                if (myRank > 0)
                {
                    var me = lb.Items.First(e => e.Username.Equals(_auth?.CurrentUsername, StringComparison.OrdinalIgnoreCase));
                    pointsText.Text = $"积分：{me.Points}";
                    rankText.Text = $"排名：#{myRank} / {lb.Total}";
                }
                else
                {
                    pointsText.Text = "积分：—（未上榜）";
                    rankText.Text = $"总成员：{lb.Total}";
                }
            }
        }
        catch
        {
            pointsText.Text = "积分：—（加载失败）";
            rankText.Text = "";
        }
    }

    private static string BuildDetail(EventLogDto l)
    {
        var parts = new List<string>();
        parts.Add(l.Result switch
        {
            "completed" => "已完成",
            "cancelled" => "已取消",
            _ => l.Result,
        });
        if (l.Mileage is > 0) parts.Add($"里程 {l.Mileage} km");
        if (l.Income is > 0) parts.Add($"收入 {l.Income:N0}");
        return string.Join(" · ", parts);
    }

    // ---------- 绑定模型 ----------

    private class EventLogItem
    {
        public string Title { get; set; } = "";
        public string Detail { get; set; } = "";
        public string TimeText { get; set; } = "";
    }

    private class LbEntry
    {
        public string RankText { get; set; } = "";
        public IBrush RankBrush { get; set; } = Brushes.Gray;
        public string Username { get; set; } = "";
        public string Plate { get; set; } = "";
        public string PointsText { get; set; } = "";
    }
}

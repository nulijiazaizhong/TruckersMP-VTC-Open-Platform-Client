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

    private bool _loaded; // 构造去重
    private volatile bool _disposed; // 页面切走后停止回写 UI（防对已脱离视觉树的控件赋值）

    public ProfileView()
    {
        try
        {
            InitializeComponent();
        }
        catch (Exception ex)
        {
            // XAML 初始化失败：记录后仍尝试构造（避免崩进程），后续绑定走空控件
            VTC.Client.Logging.Serilog?.Error(ex, "ProfileView.InitializeComponent 失败");
            throw;
        }
        if (_loaded) return;
        _loaded = true;
        var sp = AppServiceLocator.Services;
        _api = sp?.GetService<IApiClient>();
        _auth = sp?.GetService<AuthService>();
        var settings = sp?.GetService<AppSettings>();
        Unloaded += (_, _) => _disposed = true; // 离开页面即标记，pending 请求结果不再回写

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

        // 兜底：整个加载流程任何未预料的异常（含 UI 回写时控件已卸载）都写日志，
        // 绝不抛到 UI 线程导致崩溃。
        try
        {
            // ① 活动记录（C7）
            try
            {
                var logs = await _api.GetAsync<PageResult<EventLogDto>>(VtcApi.EventLogs,
                    new Dictionary<string, string?> { ["take"] = "20", ["skip"] = "0" });
                if (_disposed) return; // 页面已切走
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
            catch (Exception ex) { if (!_disposed) SafeSet(() => recordsEmpty.Text = "活动记录加载失败"); LogErr(ex); }

            // ② 排行榜（C11）
            try
            {
                var lb = await _api.GetAsync<LeaderboardDto>(VtcApi.Leaderboard,
                    new Dictionary<string, string?> { ["take"] = "10", ["skip"] = "0" }, useAuth: false);
                if (_disposed) return; // 页面已切走
                if (lb?.Items is { Count: > 0 })
                {
                    var entries = new ObservableCollection<LbEntry>();
                    LeaderboardEntryDto? meEntry = null;
                    var username = _auth?.CurrentUsername;
                    foreach (var e in lb.Items)
                    {
                        if (meEntry == null
                            && !string.IsNullOrWhiteSpace(username)
                            && e.Username != null
                            && e.Username.Equals(username, StringComparison.OrdinalIgnoreCase))
                            meEntry = e; // 收集匹配项，避免后续 First 抛异常
                        entries.Add(new LbEntry
                        {
                            RankText = e.Rank <= 3 ? $"#{e.Rank} 🏅" : $"#{e.Rank}",
                            RankBrush = e.Rank <= 3
                                ? new SolidColorBrush(Color.FromRgb(255, 193, 7))
                                : new SolidColorBrush(Color.FromRgb(136, 136, 136)),
                            Username = e.Username ?? "",
                            Plate = e.Plate ?? "",
                            PointsText = $"{e.Points} 分",
                        });
                    }
                    leaderboard.ItemsSource = entries;
                    lbEmpty.IsVisible = false;

                    // 积分与排名文本（用收集到的 meEntry，不再二次 First）
                    if (meEntry != null)
                    {
                        pointsText.Text = $"积分：{meEntry.Points}";
                        rankText.Text = $"排名：#{meEntry.Rank} / {lb.Total}";
                    }
                    else
                    {
                        pointsText.Text = "积分：—（未上榜）";
                        rankText.Text = $"总成员：{lb.Total}";
                    }
                }
            }
            catch (Exception ex)
            {
                if (!_disposed)
                {
                    SafeSet(() => pointsText.Text = "积分：—（加载失败）");
                    SafeSet(() => rankText.Text = "");
                }
                LogErr(ex);
            }
        }
        catch (Exception ex) { LogErr(ex); } // 最外层兜底：绝不向 UI 线程抛
    }

    private void SafeSet(Action a) { try { if (!_disposed) a(); } catch { } }
    private void LogErr(Exception ex) { try { System.Diagnostics.Debug.WriteLine($"ProfileView: {ex}"); } catch { } }

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

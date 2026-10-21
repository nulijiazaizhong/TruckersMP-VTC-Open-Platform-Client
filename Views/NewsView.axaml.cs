using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Microsoft.Extensions.DependencyInjection;
using VTC.Client.Services;
using RssItem = VTC.Client.Services.RssService.RssItem;

namespace VTC.Client.Views;

/// <summary>
/// C9 资讯（P1）：SCS Blog 官方 RSS，按 欧卡(ETS2) / 美卡(ATS) 分组展示，
/// 点击条目用系统浏览器打开详情页。
/// 源：feedburner Atom feed（.env 可覆盖 RSS_FEED_ETS2 / RSS_FEED_ATS）。
/// </summary>
public partial class NewsView : UserControl
{
    private RssService? _rss;
    private bool _loaded;
    private CancellationTokenSource? _cts; // 页面切走时取消 pending 加载

    public NewsView()
    {
        InitializeComponent();
        if (_loaded) return;
        _loaded = true;
        var sp = AppServiceLocator.Services;
        _rss = sp?.GetService<RssService>();
        sourceHint.Text = "来源：SCS Blog（feedburner）";
        Unloaded += (_, _) => _cts?.Cancel();
        _ = LoadAsync();
    }

    private void OnRefreshClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        // 强制刷新：删除缓存后重新拉取
        try
        {
            var cacheDir = System.IO.Path.Combine(Paths.CacheRoot, "rss");
            if (System.IO.Directory.Exists(cacheDir))
                foreach (var f in System.IO.Directory.GetFiles(cacheDir))
                    System.IO.File.Delete(f);
        }
        catch { }
        _ = LoadAsync();
    }

    // 点击条目 → 系统浏览器打开详情页
    private void OnItemPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { Tag: RssItem item }) return;
        OpenInBrowser(item.Link);
    }

    private static void OpenInBrowser(string url)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                url = "https://" + url;
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"打开浏览器失败：{ex.Message}");
        }
    }

    private async Task LoadAsync()
    {
        if (_rss == null)
        {
            emptyHint.IsVisible = true;
            emptyHint.Text = "RSS 服务不可用";
            return;
        }

        _cts?.Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;

        loadingHint.IsVisible = true;
        emptyHint.IsVisible = false;
        btnRefresh.IsEnabled = false;

        // 并行拉取 ets2 + ats 两个源
        Task<List<RssItem>> tEts = Task.Run(() => _rss.GetItemsAsync("ets2", take: 30, ct: cts.Token), cts.Token);
        Task<List<RssItem>> tAts = Task.Run(() => _rss.GetItemsAsync("ats", take: 30, ct: cts.Token), cts.Token);

        List<RssItem> ets = new(), ats = new();
        try
        {
            await Task.WhenAll(tEts, tAts);
            ets = tEts.Result ?? new();
            ats = tAts.Result ?? new();
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            if (cts.IsCancellationRequested) return;
            emptyHint.IsVisible = true;
            emptyHint.Text = $"加载失败：{ex.Message}";
            loadingHint.IsVisible = false;
            btnRefresh.IsEnabled = true;
            return;
        }

        if (cts.IsCancellationRequested) return; // 页面已切走

        // 按游戏归类（源本身可能混，按 item.Game 再分一次）
        var etsItems = ets.Where(i => i.Game == "ets2").ToList();
        var atsItems = ats.Where(i => i.Game == "ats").ToList();
        // 若某源返回的 item 游戏标签与源不符，兜底按源归类
        if (etsItems.Count == 0 && ets.Count > 0) etsItems = ets;
        if (atsItems.Count == 0 && ats.Count > 0) atsItems = ats;

        ets2List.ItemsSource = etsItems;
        atsList.ItemsSource = atsItems;
        ets2Empty.IsVisible = etsItems.Count == 0;
        atsEmpty.IsVisible = atsItems.Count == 0;
        emptyHint.IsVisible = etsItems.Count == 0 && atsItems.Count == 0;
        if (emptyHint.IsVisible)
            emptyHint.Text = "暂无资讯（RSS 源可能不可达或无新内容）";

        loadingHint.IsVisible = false;
        btnRefresh.IsEnabled = true;
    }
}

using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Microsoft.Extensions.DependencyInjection;
using VTC.Client.Services;

namespace VTC.Client.Views;

/// <summary>
/// C9 资讯（P1）：欧卡/美卡 双源 RSS + 本地缓存 + 版本标签过滤。
/// 默认源：SCS Blog feedburner（.env 可覆盖 RSS_FEED_ETS2 / RSS_FEED_ATS）。
/// </summary>
public partial class NewsView : UserControl
{
    private string _tab = "ets2";
    private RssService? _rss;
    private bool _loaded; // 实例复用安全网：页面被缓存复用时不重复初始化

    private CancellationTokenSource? _cts; // 页面被切走时取消 pending 请求，防止对已脱离视觉树的控件回写

    public NewsView()
    {
        InitializeComponent();
        if (_loaded) return;
        _loaded = true;
        var sp = AppServiceLocator.Services;
        _rss = sp?.GetService<RssService>();
        sourceHint.Text = "来源：SCS Blog（feedburner）";
        Unloaded += (_, _) => _cts?.Cancel(); // 离开页面即取消未完成的加载
        _ = LoadAsync();
    }

    private void OnTabClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag }) return;
        _tab = tag;
        var accent = App.Current.Resources["vtc_accent"] as IBrush ?? new SolidColorBrush(Color.FromRgb(30, 136, 229));
        tabEts2.Background = tag == "ets2" ? accent : Brushes.Transparent;
        tabEts2.Foreground = tag == "ets2" ? Brushes.White : new SolidColorBrush(Color.FromRgb(187, 187, 187));
        tabAts.Background = tag == "ats" ? accent : Brushes.Transparent;
        tabAts.Foreground = tag == "ats" ? Brushes.White : new SolidColorBrush(Color.FromRgb(187, 187, 187));
        sourceHint.Text = tag == "ets2" ? "来源：SCS Blog · 欧卡" : "来源：SCS Blog · 美卡";
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
        catch { /* ignore */ }
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        if (_rss == null)
        {
            emptyHint.IsVisible = true;
            emptyHint.Text = "RSS 服务不可用";
            return;
        }

        // 取消上一次未完成的加载（切 tab / 刷新时避免并发叠加）
        _cts?.Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;

        loadingHint.IsVisible = true;
        emptyHint.IsVisible = false;
        btnRefresh.IsEnabled = false;

        try
        {
            var items = await _rss.GetItemsAsync(_tab, take: 30, ct: cts.Token);
            if (cts.IsCancellationRequested) return; // 页面已切走，不再回写 UI
            list.ItemsSource = items;
            emptyHint.IsVisible = items.Count == 0;
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            emptyHint.IsVisible = true;
            emptyHint.Text = $"加载失败：{ex.Message}";
        }
        finally
        {
            if (!cts.IsCancellationRequested)
            {
                loadingHint.IsVisible = false;
                btnRefresh.IsEnabled = true;
            }
        }
    }
}

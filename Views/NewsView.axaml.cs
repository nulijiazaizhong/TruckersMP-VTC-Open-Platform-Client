using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Microsoft.Extensions.DependencyInjection;
using VTC.Client.Services;
using RssItem = VTC.Client.Services.RssService.RssItem;

namespace VTC.Client.Views;

/// <summary>
/// C9 资讯（P1）：欧卡/美卡 双源 RSS（tab 切换）+ 本地缓存 + 点击打开详情页。
/// 源：SCS Blog feedburner Atom feed（.env 可覆盖 RSS_FEED_ETS2 / RSS_FEED_ATS）。
/// </summary>
public partial class NewsView : UserControl
{
    private string _tab = "ets2";
    private RssService? _rss;
    private bool _loaded;
    private CancellationTokenSource? _cts;

    // 两个源的缓存：LoadAsync 一次拉全，切 tab 只换显示、不再请求
    private List<RssItem> _ets = new();
    private List<RssItem> _ats = new();

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

    // 切换 tab：只更新按钮高亮 + 重新绑定当前 tab 的列表（数据已缓存，不再请求）
    private void OnTabClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag }) return;
        _tab = tag;
        var accent = App.Current.Resources["vtc_accent"] as IBrush ?? new SolidColorBrush(Color.FromRgb(30, 136, 229));
        // 高亮：当前 tab 用 accent 背景 + 白字；非当前 tab 透明背景 + 灰字。
        // 注意前景必须是 White（不能是 accent）——否则文字与背景同色，按钮"消失"。
        tabEts2.Background = tag == "ets2" ? accent : Brushes.Transparent;
        tabEts2.Foreground = tag == "ets2" ? Brushes.White : new SolidColorBrush(Color.FromRgb(187, 187, 187));
        tabAts.Background = tag == "ats" ? accent : Brushes.Transparent;
        tabAts.Foreground = tag == "ats" ? Brushes.White : new SolidColorBrush(Color.FromRgb(187, 187, 187));
        sourceHint.Text = tag == "ets2" ? "来源：SCS Blog · 欧卡" : "来源：SCS Blog · 美卡";
        ApplyCurrentTab();
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

    // 点击条目 → 内嵌显示详情（不跳浏览器）；"在浏览器打开"按钮可选跳转原文
    private RssItem? _current;
    private CancellationTokenSource? _bodyCts;
    private void OnItemPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { Tag: RssItem item }) return;
        _current = item;
        _bodyCts?.Cancel();
        var cts = new CancellationTokenSource();
        _bodyCts = cts;

        detailTitle.Text = item.Title;
        detailDate.Text = item.DateText;
        detailVersion.Text = string.IsNullOrWhiteSpace(item.GameVersionTag)
            ? "" : $"版本 {item.GameVersionTag}";

        // 用 RSS body 渲染（文本秒显 + 图片异步加载）。
        // 渲染全程 try/catch：任何解析/索引异常都不允许崩掉整个客户端——
        // 退化为纯文本详情（之前 FindWrappingAHref 的 LastIndexOf 越界就崩过）。
        try
        {
            RenderBody(item);
        }
        catch (Exception ex)
        {
            VTC.Client.Logging.Serilog?.Error(ex, "[资讯详情] RenderBody 异常，回退纯文本：{Title}", item.Title);
            detailBody.Children.Clear();
            detailBody.Children.Add(new TextBlock
            {
                Text = item.DisplayBody,
                FontSize = 13,
                Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 22,
            });
        }
        list.IsVisible = false;
        emptyHint.IsVisible = false;
        detailPanel.IsVisible = true;
    }

    /// <summary>
    /// 把文章正文渲染进 detailBody（StackPanel）：文本段落 → TextBlock，
    /// &lt;img src&gt; → 真实 Image 控件（异步加载 URL）。优先用 RawHtml（含 img 标签），
    /// 无则回退纯文本 Body。
    /// </summary>
    private void RenderBody(RssItem item)
    {
        detailBody.Children.Clear();
        var html = item.RawHtml;

        if (string.IsNullOrWhiteSpace(html))
        {
            // 无 HTML：纯文本回退
            var txt = new TextBlock
            {
                Text = item.DisplayBody,
                FontSize = 13,
                Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 22,
                Margin = new Thickness(0, 0, 0, 8),
            };
            detailBody.Children.Add(txt);
            return;
        }

        // ① 关键：源 HTML 里图片 URL 常被**折行**（URL 中间有换行/空格），
        //    必须先折叠，否则正则截断到换行 → 拿到残缺 URL → 图片加载失败。
        //    对引号内的内容（属性值/URL）去掉所有空白；引号外的换行转段落换行。
        var norm = NormaliseHtml(html);

        // ② 按 <img> 切分：图片之间的文本段 + 图片本身，按原始顺序排布
        var imgRegex = new System.Text.RegularExpressions.Regex(
            @"<img\b[^>]*?\bsrc\s*=\s*([""'])(.*?)\1[^>]*?>",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        var pos = 0;
        foreach (System.Text.RegularExpressions.Match m in imgRegex.Matches(norm))
        {
            if (m.Index > pos)
                AddTextBlock(detailBody, StripHtmlText(norm.Substring(pos, m.Index - pos)));
            // src 值再兜底去空白（防漏网）
            var src = new string(m.Groups[2].Value.Where(c => !char.IsWhiteSpace(c)).ToArray());
            // 直接渲染 img 的 src（缩略图 w640-h360 实测可下载 200）。
            // 不用 <a href> 回退——LastIndexOf(count) 边界易越界崩溃，且缩略图本身有效。
            AddImage(detailBody, src);
            pos = m.Index + m.Length;
        }
        if (pos < norm.Length)
            AddTextBlock(detailBody, StripHtmlText(norm.Substring(pos)));
    }

    /// <summary>
    /// 归一化 HTML：① 引号内（属性值/URL）去掉所有空白字符（修 URL 折行）；
    /// ② 引号外的换行/块级标签转换行（保留段落）。
    /// </summary>
    private static string NormaliseHtml(string html)
    {
        // 用引号把字符串切成「引号外 / 引号内」交替片段
        var sb = new System.Text.StringBuilder();
        bool inQuote = false;
        char quote = '\0';
        foreach (var c in html)
        {
            if (inQuote)
            {
                if (c == quote) { inQuote = false; sb.Append(c); }
                else if (!char.IsWhiteSpace(c)) sb.Append(c);   // 引号内：丢所有空白
                // 换行/空格直接丢弃（URL 折行修复）
            }
            else
            {
                if (c == '"' || c == '\'') { inQuote = true; quote = c; sb.Append(c); }
                else sb.Append(c);
            }
        }
        return sb.ToString();
    }

    private static void AddTextBlock(Avalonia.Controls.StackPanel host, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        host.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 22,
            Margin = new Thickness(0, 0, 0, 10),
        });
    }

    private static void AddImage(Avalonia.Controls.StackPanel host, string src, string? fallback = null)
    {
        // 主图与回退图都是无效/相对 URL 时跳过
        if (!IsHttpUrl(src) && !IsHttpUrl(fallback)) return;
        var img = new Image
        {
            Stretch = Stretch.Uniform,
            MaxHeight = 420,
            MinHeight = 40, // 加载前占位高度，避免空白跳动
            Margin = new Thickness(0, 0, 0, 12),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        // 异步加载：先试 src（缩略图），失败回退 fallback（原图）
        _ = LoadImageAsync(img, src, fallback);
        host.Children.Add(img);
    }

    private static bool IsHttpUrl(string? u) =>
        !string.IsNullOrWhiteSpace(u) && u.StartsWith("http", StringComparison.OrdinalIgnoreCase);

    private static async System.Threading.Tasks.Task LoadImageAsync(Image img, string url, string? fallback = null)
    {
        // 依次尝试：主图 → 回退图
        var candidates = new List<string>();
        if (IsHttpUrl(url)) candidates.Add(url);
        if (IsHttpUrl(fallback) && fallback != url) candidates.Add(fallback!);

        foreach (var u in candidates)
        {
            try
            {
                using var http = new System.Net.Http.HttpClient { Timeout = System.TimeSpan.FromSeconds(20) };
                var bytes = await http.GetByteArrayAsync(u);
                if (bytes.Length == 0) continue;
                using var ms = new System.IO.MemoryStream(bytes);
                var bmp = new Avalonia.Media.Imaging.Bitmap(ms);
                VTC.Client.Logging.Serilog?.Information("[资讯图片] 下载成功 {Size}字节 {Px}x{Py} {Url}",
                    bytes.Length, bmp.PixelSize.Width, bmp.PixelSize.Height,
                    u.Length > 70 ? u.Substring(0, 70) + "…" : u);
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    img.Source = bmp;
                    img.MinHeight = 0; // 加载成功，移除占位
                });
                return; // 成功
            }
            catch (Exception ex)
            {
                VTC.Client.Logging.Serilog?.Warning("[资讯图片] 下载失败 {Url}: {Ex}",
                    u.Length > 70 ? u.Substring(0, 70) + "…" : u, ex.Message);
            }
        }

        // 全部失败：收起占位（不留空白）
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            img.Source = null;
            img.Height = 0;
            img.MinHeight = 0;
        });
    }

    /// <summary>
    /// 去 HTML 标签（详情页文本段用，保留换行）。
    /// 先把块级标签换行、再删**所有**标签（含 &lt;a href="长URL"&gt;——否则属性文本会露出），
    /// 再解码实体、折叠空白。
    /// </summary>
    private static string StripHtmlText(string html)
    {
        if (string.IsNullOrEmpty(html)) return "";
        var t = html;
        // 块级结束/开始 → 换行（保留段落结构）
        t = System.Text.RegularExpressions.Regex.Replace(t,
            @"</?(p|div|ul|ol|li|h[1-6]|blockquote|table|tr|br)\b[^>]*>", "\n");
        // 删除所有剩余标签（a/img/span/strong 等，含其属性）
        t = System.Text.RegularExpressions.Regex.Replace(t, @"<[^>]*>", "");
        // 解码常见实体
        t = t.Replace("&nbsp;", " ").Replace("&amp;", "&")
             .Replace("&lt;", "<").Replace("&gt;", ">").Replace("&quot;", "\"").Replace("&#39;", "'");
        // 折叠行内空白（保留换行）
        t = System.Text.RegularExpressions.Regex.Replace(t, @"[ \t]+", " ");
        t = System.Text.RegularExpressions.Regex.Replace(t, @" ?\n ?", "\n");
        t = System.Text.RegularExpressions.Regex.Replace(t, @"\n{3,}", "\n\n");
        return t.Trim();
    }

    // 返回资讯列表
    private void OnBackClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        detailPanel.IsVisible = false;
        list.IsVisible = true;
        emptyHint.IsVisible = (_tab == "ets2" ? _ets : _ats).Count == 0;
    }

    // 在浏览器打开当前文章原文
    private void OnBrowserClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_current != null) OpenInBrowser(_current.Link);
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

    /// <summary>把当前 tab 对应的列表绑定到 list（数据来自 _ets/_ats 缓存）。</summary>
    private void ApplyCurrentTab()
    {
        // 切 tab / 刷新时若正开着详情，先回到列表视图
        if (detailPanel.IsVisible)
        {
            detailPanel.IsVisible = false;
            list.IsVisible = true;
        }
        var items = _tab == "ets2" ? _ets : _ats;
        list.ItemsSource = items;
        list.IsVisible = true;
        emptyHint.IsVisible = items.Count == 0;
        if (emptyHint.IsVisible)
            emptyHint.Text = _tab == "ets2" ? "暂无欧卡资讯" : "暂无美卡资讯";
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

        // 并行拉取 ets2 + ats 两个源（各带 30min 本地缓存，缓存命中时不占网络）
        Task<List<RssItem>> tEts = Task.Run(() => _rss.GetItemsAsync("ets2", take: 30, ct: cts.Token), cts.Token);
        Task<List<RssItem>> tAts = Task.Run(() => _rss.GetItemsAsync("ats", take: 30, ct: cts.Token), cts.Token);

        try
        {
            await Task.WhenAll(tEts, tAts);
            _ets = tEts.Result ?? new();
            _ats = tAts.Result ?? new();
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

        // 按源归类（源本身可能混标签，按 item.Game 校正；若源标签全不符则整体归当前源）
        _ets = _ets.Where(i => i.Game == "ets2").ToList();
        _ats = _ats.Where(i => i.Game == "ats").ToList();
        var rawEts = tEts.Result ?? new();
        var rawAts = tAts.Result ?? new();
        if (_ets.Count == 0 && rawEts.Count > 0) _ets = rawEts;
        if (_ats.Count == 0 && rawAts.Count > 0) _ats = rawAts;

        ApplyCurrentTab();
        loadingHint.IsVisible = false;
        btnRefresh.IsEnabled = true;
    }
}

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

        // ② 按 <img> 和 <iframe> 切分，按原始顺序排布：
        //    · <img src> → 真实图片
        //    · <iframe src=".../widget/{appId}/"> → Steam 商店卡片（异步查 API 渲染）
        var imgRe = new System.Text.RegularExpressions.Regex(
            @"<img\b[^>]*?\bsrc\s*=\s*([""'])(.*?)\1[^>]*?>",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var iframeRe = new System.Text.RegularExpressions.Regex(
            @"<iframe\b[^>]*?\bsrc\s*=\s*([""'])(.*?)\1[^>]*?>",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // 合并两个匹配流，按位置排序
        var tokens = new List<(int index, int length, string kind, string value)>();
        foreach (System.Text.RegularExpressions.Match m in imgRe.Matches(norm))
            tokens.Add((m.Index, m.Length, "img",
                new string(m.Groups[2].Value.Where(c => !char.IsWhiteSpace(c)).ToArray())));
        foreach (System.Text.RegularExpressions.Match m in iframeRe.Matches(norm))
            tokens.Add((m.Index, m.Length, "iframe",
                new string(m.Groups[2].Value.Where(c => !char.IsWhiteSpace(c)).ToArray())));
        tokens = tokens.OrderBy(x => x.index).ToList();

        var pos = 0;
        foreach (var tok in tokens)
        {
            if (tok.index > pos)
                AddMarkup(detailBody, norm.Substring(pos, tok.index - pos));

            if (tok.kind == "img")
            {
                // 图片：直接渲染 src（缩略图 w640-h360 实测 200 可下载）
                AddImage(detailBody, tok.value);
            }
            else // iframe
            {
                // Steam 商店 widget：src 形如 https://store.steampowered.com/widget/{appId}/
                // 提取 AppID → 异步查 Steam API → 渲染商店卡片（封面+名称+价格+购买链接）
                var appId = ExtractSteamAppId(tok.value);
                if (appId != null)
                    AddSteamCard(detailBody, appId);
                // 非 Steam 的 iframe（如 YouTube embed）：不渲染（避免空白），跳过
            }
            pos = tok.index + tok.length;
        }
        if (pos < norm.Length)
            AddMarkup(detailBody, norm.Substring(pos));
    }

    /// <summary>从 iframe src 提取 Steam AppID（store.steampowered.com/widget/{appId}/）。非 Steam 返回 null。</summary>
    private static string? ExtractSteamAppId(string src)
    {
        if (string.IsNullOrWhiteSpace(src)) return null;
        var m = System.Text.RegularExpressions.Regex.Match(
            src, @"store\.steampowered\.com/widget/(\d+)/?",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    // 行类型标记：删标签前先替换成不可见标记，删完标签后按标记识别行类型（可靠，不靠回查）
    private const string MARK_LI = "\u0001LI\u0001";     // 列表项
    private const string MARK_H  = "\u0001H\u0001";       // 标题

    /// <summary>
    /// 类 Markdown 结构化渲染：把 HTML 段按块级标签切成"行"，每行按类型套用样式——
    /// · 标题（&lt;h1-6&gt;）→ 加粗 + 强调橙；列表项 &lt;li&gt; → "• " 项目符号 + 左缩进；
    /// · 普通段落 → 正文样式。changelog 的"分类标题 + 列表"会以清晰 Markdown 层级呈现。
    /// 用**标记法**识别行类型（删标签前替换 &lt;li&gt;/&lt;hN&gt; 为标记），删完标签后按标记分行。
    /// </summary>
    private static void AddMarkup(Avalonia.Controls.StackPanel host, string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return;

        var t = html;
        // ① 删标签前打标记（保留结构信息）
        t = System.Text.RegularExpressions.Regex.Replace(t,
            @"<li[^>]*>", MARK_LI, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        t = System.Text.RegularExpressions.Regex.Replace(t,
            @"<h[1-6]\b[^>]*>", MARK_H, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        // 注：changelog 分类标题（Map/Vehicles/Sound/Changelog:）不在此用正则标——
        // 它们的嵌套结构多变（<p><b><span color>、<div><span color>、<div><p><span color>），
        // 改用切行后「逐行回查 color 样式」判断（见下方 IsColorHeading），更通用可靠。

        // ② 块级标签 → 换行（切行）。关键：含 </li>——否则相邻 <li> 粘连成一行。
        t = System.Text.RegularExpressions.Regex.Replace(t,
            @"</?(p|div|ul|ol|h[1-6]|blockquote|table|tr|section|article|li)\b[^>]*>", "\n");
        t = System.Text.RegularExpressions.Regex.Replace(t, @"<br\s*/?>", "\n");
        // ③ 标签边界补空格（防粘连），再删剩余标签
        t = System.Text.RegularExpressions.Regex.Replace(t, @"(?<=[^\s<])<[^>]*>(?=[^\s>])", " ");
        t = System.Text.RegularExpressions.Regex.Replace(t, @"<[^>]*>", "");
        // ④ 解码实体 + 折叠行内空白（保留换行与标记）
        t = t.Replace("&nbsp;", " ").Replace("&amp;", "&")
             .Replace("&lt;", "<").Replace("&gt;", ">").Replace("&quot;", "\"").Replace("&#39;", "'");
        t = System.Text.RegularExpressions.Regex.Replace(t, @"[ \t]+", " ");
        t = System.Text.RegularExpressions.Regex.Replace(t, @" ?\n ?", "\n");
        t = System.Text.RegularExpressions.Regex.Replace(t, @"\n{3,}", "\n\n");

        // ⑤ 逐行套 Markdown 风格
        var accent = new SolidColorBrush(Color.FromRgb(0xF1, 0xC2, 0x32)); // SCS 强调橙 #f1c232
        var bodyColor = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC));

        foreach (var rawLine in t.Split('\n'))
        {
            var isLi = rawLine.Contains(MARK_LI);
            var line = rawLine.Replace(MARK_LI, "").Replace(MARK_H, "").Trim();
            if (line.Length == 0) continue;
            // 标题 = 行带 <hN> 标记，或（短行 且 原始 HTML 该行带 color 样式——changelog 分类标题）
            var isH = rawLine.Contains(MARK_H) || (!isLi && IsColorHeading(line, html));

            if (isLi)
            {
                host.Children.Add(new TextBlock
                {
                    Text = "•  " + line,
                    FontSize = 13,
                    Foreground = bodyColor,
                    TextWrapping = TextWrapping.Wrap,
                    LineHeight = 22,
                    Margin = new Thickness(18, 2, 0, 2),
                });
            }
            else if (isH)
            {
                host.Children.Add(new TextBlock
                {
                    Text = line,
                    FontSize = 14,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = accent,
                    TextWrapping = TextWrapping.Wrap,
                    LineHeight = 22,
                    Margin = new Thickness(0, 12, 0, 4),
                });
            }
            else
            {
                host.Children.Add(new TextBlock
                {
                    Text = line,
                    FontSize = 13,
                    Foreground = bodyColor,
                    TextWrapping = TextWrapping.Wrap,
                    LineHeight = 22,
                    Margin = new Thickness(0, 0, 0, 10),
                });
            }
        }
    }

    /// <summary>
    /// 判断一行是否为 changelog 分类标题：短行（≤40 字符，排除长段落），
    /// 且其文本在原始 HTML 中被**带 color 样式的 span** 覆盖。
    /// 覆盖三种嵌套：&lt;p&gt;&lt;b&gt;&lt;span color&gt;、&lt;div&gt;&lt;span color&gt;、&lt;div&gt;&lt;p&gt;&lt;span color&gt;。
    /// 用行首特征词回查原始 HTML 上下文窗口，检查窗口内有 color 样式且文本在 span 内。
    /// </summary>
    private static bool IsColorHeading(string line, string originalHtml)
    {
        if (line.Length == 0 || line.Length > 40) return false;
        // 短行（如 "Map"）整行作 probe；长行取前 12 字符。
        // 关键：短词可能在 HTML 多处出现，需**遍历所有匹配**，只要有一个落在带 color 的 span 内即判定为标题。
        var probe = line.Length <= 12 ? line : line.Substring(0, 12);
        var from = 0;
        while (true)
        {
            var i = originalHtml.IndexOf(probe, from, System.StringComparison.OrdinalIgnoreCase);
            if (i < 0) return false;
            if (IsInsideColorSpan(originalHtml, i)) return true;
            from = i + 1; // 该匹配不在 color span 内，找下一个
        }
    }

    /// <summary>位置 pos 处的文本是否被一个「带 color 样式且尚未闭合」的 &lt;span&gt; 覆盖。</summary>
    private static bool IsInsideColorSpan(string html, int pos)
    {
        var winStart = Math.Max(0, pos - 120);
        var window = html.Substring(winStart, pos - winStart);
        var lastSpan = window.LastIndexOf("<span", System.StringComparison.OrdinalIgnoreCase);
        if (lastSpan < 0) return false;
        var spanTag = window.Substring(lastSpan);
        return spanTag.Contains("color", System.StringComparison.OrdinalIgnoreCase)
            && !spanTag.Contains("</span>", System.StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Steam 商店卡片：替代文章里的 Steam widget &lt;iframe&gt;。
    /// 立即放一个占位 Border，异步查 Steam 公开 API（store.steampowered.com/api/appdetails）
    /// 拿名称 + 封面 + 价格，渲染成「封面图 + 名称 + 价格 + 在 Steam 查看」卡片。
    /// </summary>
    private static void AddSteamCard(Avalonia.Controls.StackPanel host, string appId)
    {
        // 占位（异步填充前显示"加载中"）
        var card = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x22, 0x2A)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x40, 0x4A)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(0, 4, 0, 14),
            Padding = new Thickness(10, 8, 10, 8),
        };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto, *"), RowDefinitions = new RowDefinitions("*") };
        // 左：封面（先占位）。Avalonia 11 的 Image 无 CornerRadius，圆角用 Border 包裹。
        var cover = new Image
        {
            Width = 92, Height = 26, Stretch = Stretch.UniformToFill,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 12, 0),
        };
        Grid.SetColumn(cover, 0); Grid.SetRow(cover, 0);
        // 右：名称 + 价格 + 按钮（占位）
        var right = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var nameTb = new TextBlock
        {
            Text = "Steam 商店加载中…",
            FontSize = 14, FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0xEE, 0xEE, 0xEE)),
        };
        var priceTb = new TextBlock
        {
            Text = "",
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromRgb(0xB0, 0xC4, 0xD6)),
            Margin = new Thickness(0, 4, 0, 0),
        };
        // "在 Steam 查看" 链接按钮
        var linkTb = new TextBlock
        {
            Text = "在 Steam 上查看 ↗",
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0xC0, 0x44)),
            Margin = new Thickness(0, 6, 0, 0),
        };
        // 点击 → 用系统默认浏览器打开 Steam 商店页（Process.Start 跨平台可靠）
        linkTb.PointerPressed += (s, e) =>
        {
            try
            {
                var url = $"https://store.steampowered.com/app/{appId}/";
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url)
                { UseShellExecute = true });
            }
            catch { }
        };
        right.Children.Add(nameTb);
        right.Children.Add(priceTb);
        right.Children.Add(linkTb);
        Grid.SetColumn(right, 1); Grid.SetRow(right, 0);
        grid.Children.Add(cover);
        grid.Children.Add(right);
        card.Child = grid;
        host.Children.Add(card);

        // 异步查 Steam API 填充
        _ = LoadSteamCardAsync(cover, nameTb, priceTb, appId);
    }

    private static async System.Threading.Tasks.Task LoadSteamCardAsync(
        Image cover, TextBlock nameTb, TextBlock priceTb, string appId)
    {
        try
        {
            using var http = new System.Net.Http.HttpClient { Timeout = System.TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
            var json = await http.GetStringAsync($"https://store.steampowered.com/api/appdetails?appids={appId}&cc=CN");
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty(appId, out var appEl)) return;
            if (!appEl.TryGetProperty("data", out var data)) return;

            string name = data.TryGetProperty("name", out var n) ? n.GetString() ?? "Steam 应用" : "Steam 应用";
            string coverUrl = data.TryGetProperty("header_image", out var c) ? c.GetString() ?? "" : "";
            string price = "";
            bool isFree = data.TryGetProperty("is_free", out var f) && f.GetBoolean();
            if (isFree) price = "免费";
            else if (data.TryGetProperty("price_overview", out var po) && po.TryGetProperty("final_formatted", out var ff))
                price = ff.GetString() ?? "";

            // 回 UI 线程填充
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                nameTb.Text = name;
                priceTb.Text = price;
                priceTb.IsVisible = !string.IsNullOrWhiteSpace(price);
                if (!string.IsNullOrWhiteSpace(coverUrl) && coverUrl.StartsWith("http"))
                    _ = LoadSteamCoverAsync(cover, coverUrl);
            });
        }
        catch
        {
            // API 失败：保留"加载中"或收起——收起避免误导
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                nameTb.Text = "Steam 商店信息加载失败";
            });
        }
    }

    private static async System.Threading.Tasks.Task LoadSteamCoverAsync(Image cover, string url)
    {
        try
        {
            using var http = new System.Net.Http.HttpClient { Timeout = System.TimeSpan.FromSeconds(15) };
            var bytes = await http.GetByteArrayAsync(url);
            using var ms = new System.IO.MemoryStream(bytes);
            var bmp = new Avalonia.Media.Imaging.Bitmap(ms);
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => cover.Source = bmp);
        }
        catch { /* 封面加载失败：卡片仍显示名称+价格 */ }
    }

    /// <summary>
    /// 归一化 HTML：① 引号内（属性值/URL）去掉所有空白字符（修 URL 折行）；
    /// ② 引号外的换行/块级标签转换行（保留段落）。
    /// </summary>
    private static string NormaliseHtml(string html)
    {
        // 归一化：只对**双引号**包裹的属性值去空白（修 URL 折行），其余原样保留。
        // 关键：**单引号不当引号边界**——Blogger 正文里有大量撇号（Scotland's / you'll），
        // 若把 ' 当引号起点，后续整段文本会被误判为"引号内"→ 空格全被删 → 单词粘连。
        // HTML 属性值一律用双引号包裹，单引号只出现在正文文本里，必须保留。
        var sb = new System.Text.StringBuilder();
        bool inAttr = false;   // 是否在双引号属性值内
        foreach (var c in html)
        {
            if (inAttr)
            {
                if (c == '"') { inAttr = false; sb.Append(c); }
                else if (!char.IsWhiteSpace(c)) sb.Append(c);  // 属性值内：丢空白（修 URL 折行）
            }
            else
            {
                if (c == '"') { inAttr = true; sb.Append(c); }
                else sb.Append(c);  // 正文/标签：原样保留（含单引号撇号、单词间空格）
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
        // 删除所有剩余标签（a/img/span/strong 等，含其属性）。
        // 关键：若标签**前后都是非空白字符**（word1</span>word2），删标签会粘连成 word1word2，
        // 此时补一个空格；否则（前后已有空白/行首行尾）不补，避免多余空格。
        t = System.Text.RegularExpressions.Regex.Replace(t,
            @"(?<=[^\s<])<[^>]*>(?=[^\s>])", " ");
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

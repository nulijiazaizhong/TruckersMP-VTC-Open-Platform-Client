using System.IO;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using VTC.Client;

namespace VTC.Client.Services;

/// <summary>
/// C9 资讯 RSS（P1）：欧卡(ETS2) / 美卡(ATS) 双源抓取 + 本地缓存 + 版本标签过滤。
/// 默认源：SCS Blog feedburner（https://feeds.feedburner.com/ScsSoftwaresBlog）。
/// 车队可在 .env 中覆盖 RSS_FEED_ETS2 / RSS_FEED_ATS。
/// </summary>
public sealed class RssService
{
    private readonly IApiClient _api;
    private readonly IConfiguration _cfg;
    private readonly ILogger<RssService> _log;
    private readonly HttpClient _http;
    private readonly string _cacheDir;

    public RssService(IApiClient api, IConfiguration cfg, ILogger<RssService> log)
    {
        _api = api;
        _cfg = cfg;
        _log = log;
        _http = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        })
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
        _cacheDir = Path.Combine(Paths.CacheRoot, "rss");
        Directory.CreateDirectory(_cacheDir);
    }

    public sealed class RssItem
    {
        public string Title { get; set; } = "";
        public string Link { get; set; } = "";
        public string? Summary { get; set; }
        /// <summary>完整正文（去 HTML 标签、保留段落换行、不截断）——纯文本回退用。</summary>
        public string? Body { get; set; }
        /// <summary>原始正文 HTML（含 &lt;img src&gt; 等标签）——详情页渲染真实图片用。</summary>
        public string? RawHtml { get; set; }
        public DateTime PublishedAt { get; set; }
        /// <summary>游戏版本标签（如 "1.61"），用于过滤。</summary>
        public string? GameVersionTag { get; set; }
        /// <summary>所属游戏：ets2 / ats / unknown。</summary>
        public string Game { get; set; } = "unknown";
        /// <summary>显示用日期文本。</summary>
        public string DateText => PublishedAt.ToLocalTime().ToString("yyyy-MM-dd");
        /// <summary>XAML 绑定：摘要是否可见。</summary>
        public bool HasSummary => !string.IsNullOrWhiteSpace(Summary);
        /// <summary>详情页显示用正文：优先 Body，回退 Summary，再回退提示。</summary>
        public string DisplayBody =>
            !string.IsNullOrWhiteSpace(Body) ? Body
            : !string.IsNullOrWhiteSpace(Summary) ? Summary!
            : "（此源未提供正文，请在浏览器打开原文查看完整内容）";
    }

    /// <summary>默认 SCS Blog feedburner 源。</summary>
    private const string DefaultFeed = "https://feeds.feedburner.com/ScsSoftwaresBlog";

    /// <summary>获取某游戏的 RSS 列表（本地缓存优先，30 分钟过期）。</summary>
    public async Task<List<RssItem>> GetItemsAsync(string game, int take = 30, CancellationToken ct = default)
    {
        var feedUrl = GetFeedUrl(game);

        // ① 尝试本地缓存
        var cacheFile = Path.Combine(_cacheDir, $"{game}_{take}.json");
        if (File.Exists(cacheFile))
        {
            var age = DateTime.Now - File.GetLastWriteTime(cacheFile);
            if (age < TimeSpan.FromMinutes(30))
            {
                try
                {
                    var cached = JsonSerializer.Deserialize<List<RssItem>>(File.ReadAllText(cacheFile));
                    if (cached is { Count: > 0 })
                    {
                        _log.LogDebug("RSS 缓存命中：{Game}（{Age}）", game, age.ToString(@"hh\:mm\:ss"));
                        return cached;
                    }
                }
                catch (Exception ex)
                {
                    _log.LogWarning("RSS 缓存读取失败，重新抓取：{Ex}", ex.Message);
                }
            }
        }

        // ② 在线抓取
        List<RssItem> items;
        try
        {
            _log.LogInformation("抓取 RSS：{Game} → {Url}", game, feedUrl);
            var xml = await _http.GetStringAsync(feedUrl, ct);
            items = ParseRss(xml, game);
        }
        catch (Exception ex)
        {
            _log.LogWarning("RSS 抓取失败（{Game}）：{Ex}", game, ex.Message);
            // 回退：即使过期也用旧缓存
            if (File.Exists(cacheFile))
            {
                try
                {
                    var stale = JsonSerializer.Deserialize<List<RssItem>>(File.ReadAllText(cacheFile));
                    if (stale is { Count: > 0 })
                    {
                        _log.LogInformation("使用过期 RSS 缓存：{Game}", game);
                        return stale;
                    }
                }
                catch { /* ignore */ }
            }
            return [];
        }

        // ③ 写缓存
        try
        {
            File.WriteAllText(cacheFile, JsonSerializer.Serialize(items));
        }
        catch (Exception ex)
        {
            _log.LogWarning("RSS 缓存写入失败：{Ex}", ex.Message);
        }

        return items;
    }

    /// <summary>按版本标签过滤（如只显示 1.61 的帖子）。</summary>
    public List<RssItem> FilterByVersion(List<RssItem> items, string? versionMajor)
    {
        if (string.IsNullOrWhiteSpace(versionMajor)) return items;
        return items.Where(i =>
            i.GameVersionTag == null ||          // 无标签的通用帖子保留
            i.GameVersionTag.StartsWith(versionMajor, StringComparison.OrdinalIgnoreCase)
        ).ToList();
    }

    private string GetFeedUrl(string game)
    {
        var env = EnvLoader.Get(_cfg, game == "ets2" ? "RSS_FEED_ETS2" : "RSS_FEED_ATS");
        return string.IsNullOrWhiteSpace(env) ? DefaultFeed : env!;
    }

    /// <summary>
    /// 解析 RSS 2.0（&lt;item&gt;）或 Atom（&lt;entry&gt;）→ RssItem 列表。
    /// feedburner 的 SCS Blog 现返回 **Atom**（命名空间 http://www.w3.org/2005/Atom，
    /// 条目 &lt;entry&gt;、链接在 &lt;link href&gt;、标签在 &lt;category term&gt;、正文 &lt;content type='html'&gt;），
    /// 早期 RSS 2.0 源（&lt;item&gt;/&lt;link&gt;/&lt;description&gt;/&lt;pubDate&gt;）也需兼容。
    /// </summary>
    internal static List<RssItem> ParseRss(string xml, string defaultGame)
    {
        var items = new List<RssItem>();
        try
        {
            var doc = XDocument.Parse(xml);
            // Atom 命名空间（本地名匹配用 LocalName，避免命名空间前缀问题）
            var isAtom = doc.Root?.Name.LocalName == "feed";

            // 统一取本地名子元素值
            string Local(XElement? parent, string name) =>
                parent?.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value?.Trim() ?? "";

            IEnumerable<XElement> entries = isAtom
                ? doc.Descendants().Where(e => e.Name.LocalName == "entry")
                : doc.Descendants().Where(e => e.Name.LocalName == "item");

            foreach (var item in entries.Take(50))
            {
                var title = Local(item, "title");

                // 链接：RSS <link>text</link>；Atom <link href="..."/>（取 rel≠alternate 的第一个，或第一个）
                string link = "";
                var linkEl = item.Elements().FirstOrDefault(e => e.Name.LocalName == "link");
                if (linkEl != null)
                {
                    var href = linkEl.Attribute("href")?.Value;
                    link = string.IsNullOrWhiteSpace(href) ? (linkEl.Value ?? "") : href;
                }

                // 正文：RSS <description>；Atom <content type='html'> 或 <summary>
                string body = "";
                var contentEl = item.Elements().FirstOrDefault(e =>
                    e.Name.LocalName is "content" or "summary" or "description");
                body = contentEl?.Value?.Trim() ?? "";
                if (string.IsNullOrEmpty(body)) body = Local(item, "description");

                // 日期：RSS <pubDate>；Atom <published>/<updated>
                var pubDate = Local(item, "pubDate")
                    ?? Local(item, "published")
                    ?? Local(item, "updated");
                DateTime date;
                if (DateTime.TryParse(pubDate, out var d)) date = d;
                else if (DateTime.TryParseExact(pubDate, "R", null, System.Globalization.DateTimeStyles.None, out d)) date = d;
                else date = DateTime.Now;

                // 推断游戏：优先 Atom <category term>（"Euro Truck Simulator 2"/"American Truck Simulator"），
                // 回退标题关键词
                var game = defaultGame;
                var cats = string.Join(" ", item.Elements()
                    .Where(e => e.Name.LocalName == "category")
                    .Select(e => e.Attribute("term")?.Value ?? e.Value ?? ""));
                var lower = (title + " " + cats).ToLowerInvariant();
                if (lower.Contains("american truck") || lower.Contains(" ats")) game = "ats";
                else if (lower.Contains("euro truck") || lower.Contains("ets2")) game = "ets2";

                // 提取版本标签：匹配 "1.61" "1.60" 等
                string? versionTag = null;
                var verMatch = System.Text.RegularExpressions.Regex.Match(title + " " + body, @"\b(\d+\.\d+)\b");
                if (verMatch.Success) versionTag = verMatch.Groups[1].Value;

                // 清理正文 HTML 标签 + 解码实体（&amp; &lt; &nbsp; 等）
                // Body：完整正文（保留段落换行，详情页用）；Summary：截断 200 字（列表用）
                var full = StripHtml(body, preserveBlocks: true);
                var summary = StripHtml(body, preserveBlocks: false);
                if (summary.Length > 200) summary = summary[..200] + "…";

                items.Add(new RssItem
                {
                    Title = title,
                    Link = link.Trim(),
                    Summary = string.IsNullOrWhiteSpace(summary) ? null : summary,
                    Body = string.IsNullOrWhiteSpace(full) ? null : full,
                    RawHtml = string.IsNullOrWhiteSpace(body) ? null : body, // 原始 HTML，详情页渲染图片
                    PublishedAt = date,
                    GameVersionTag = versionTag,
                    Game = game,
                });
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"RSS parse error: {ex.Message}");
        }
        return items;
    }

    /// <summary>去 HTML 标签 + 解码常见实体 + 折叠空白。
    /// preserveBlocks=true 时把块级标签（p/br/div/ul/li/h1-h6）转换行、保留段落结构（详情页用）。</summary>
    private static string StripHtml(string s, bool preserveBlocks = false)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var t = s;
        if (preserveBlocks)
        {
            // 块级结束 → 换行；<br> → 换行；列表项 → "• " 前缀
            t = System.Text.RegularExpressions.Regex.Replace(t,
                @"</?(p|div|ul|ol|h[1-6]|blockquote|table|tr)[^>]*>", "\n");
            t = System.Text.RegularExpressions.Regex.Replace(t, @"<li[^>]*>", "\n• ");
            t = System.Text.RegularExpressions.Regex.Replace(t, @"<br\s*/?>", "\n");
        }
        else
        {
            t = System.Text.RegularExpressions.Regex.Replace(t, @"<[^>]+>", " ");
        }
        // 解码常见实体
        t = t.Replace("&nbsp;", " ").Replace("&amp;", "&")
             .Replace("&lt;", "<").Replace("&gt;", ">").Replace("&quot;", "\"").Replace("&#39;", "'");
        if (preserveBlocks)
        {
            // 折叠行内空白（保留换行）
            t = System.Text.RegularExpressions.Regex.Replace(t, @"[ \t]+", " ");
            t = System.Text.RegularExpressions.Regex.Replace(t, @" ?\n ?", "\n");
            t = System.Text.RegularExpressions.Regex.Replace(t, @"\n{3,}", "\n\n");
            t = t.Trim();
        }
        else
        {
            t = System.Text.RegularExpressions.Regex.Replace(t, @"\s+", " ").Trim();
        }
        return t;
    }
}

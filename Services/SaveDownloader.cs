using System.IO;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SharpCompress.Common;
using VTC.Shared.Dtos;

namespace VTC.Client.Services;

/// <summary>
/// 存档下载（C3）。
/// - 分片下载（HTTP Range，默认 8MB/片）；
/// - 断点续传：本地 .part 文件 + sidecar .part.meta（已下字节 + 服务端 sha256 + 总大小）；
/// - sha256 校验：下载完成对比服务端元数据，失败删 .part 重下；
/// - 可选：识别 .zip/.7z 包并解压到目标目录（存档为目录形态）。
/// </summary>
public sealed class SaveDownloader
{
    private readonly HttpClient _http;
    private readonly ILogger<SaveDownloader> _log;

    public const int DefaultChunkBytes = 8 * 1024 * 1024; // 8MB

    /// <summary>
    /// 复用 <see cref="ApiClient"/> 的 HttpClient（统一 SocketsHttpHandler 配置：压缩/连接池/超时），
    /// 不从 DI 解析孤立的 HttpClient（容器未注册，启动首页会抛 "Unable to resolve HttpClient"）。
    /// </summary>
    public SaveDownloader(IApiClient api, ILogger<SaveDownloader> log)
    {
        _http = api.Http;
        _log = log;
    }

    /// <summary>
    /// 下载到目标文件。返回 (ok, 最终文件路径)。
    /// </summary>
    public async Task<(bool ok, string path, string? error)> DownloadToFileAsync(
        string url, string expectedSha256, string destFile,
        IProgress<DownloadProgress>? progress = null,
        int chunkBytes = DefaultChunkBytes,
        CancellationToken ct = default)
    {
        destFile = FileUtil.EnsureDir(Path.GetDirectoryName(destFile)!) + Path.DirectorySeparatorChar + Path.GetFileName(destFile);
        var part = destFile + ".part";
        var meta = part + ".meta";

        long totalSize = -1;
        var freshStart = 0L;

        // 读取 sidecar：若存在且与本次 url 一致，可断点续传
        if (File.Exists(part) && File.Exists(meta))
        {
            try
            {
                var m = JsonSerializer.Deserialize<PartMeta>(File.ReadAllText(meta));
                if (m != null &&
                    m.Sha256 == expectedSha256 &&
                    (m.Url == null || m.Url == url))
                {
                    freshStart = Math.Min(m.Downloaded, new FileInfo(part).Length);
                    _log.LogInformation("断点续传：{Part} 已有 {Bytes} 字节", part, freshStart);
                }
            }
            catch { freshStart = 0; }
        }

        var sha = FileUtil.CreateSha256();
        // 若断点续传，需先对已有字节补算哈希
        if (freshStart > 0)
        {
            using (var fs = File.OpenRead(part))
            {
                var buf = new byte[chunkBytes];
                long read = 0;
                while (read < freshStart)
                {
                    var n = fs.Read(buf, 0, (int)Math.Min(chunkBytes, freshStart - read));
                    if (n <= 0) break;
                    sha.TransformBlock(buf, 0, n, null, 0);
                    read += n;
                }
            }
        }

        long downloaded = freshStart;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var lastReport = DateTime.MinValue;

        using (var fs = new FileStream(part, freshStart > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var buf = new byte[chunkBytes];
            while (!ct.IsCancellationRequested)
            {
                HttpResponseMessage? resp = null;
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, new Uri(url));
                    var wantResume = downloaded > 0;
                    if (wantResume)
                        req.Headers.TryAddWithoutValidation("Range", $"bytes={downloaded}-");
                    resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

                    if (wantResume && resp.StatusCode == HttpStatusCode.OK)
                    {
                        // 服务器不支持 Range：从头下
                        _log.LogWarning("服务器不支持 Range，重新开始下载");
                        resp.Dispose();
                        downloaded = 0;
                        sha = FileUtil.CreateSha256();
                        fs.SetLength(0);
                        using var req2 = new HttpRequestMessage(HttpMethod.Get, new Uri(url));
                        resp = await _http.SendAsync(req2, HttpCompletionOption.ResponseHeadersRead, ct);
                    }

                    if (resp.StatusCode != HttpStatusCode.OK && resp.StatusCode != (HttpStatusCode)206)
                        throw new ApiException($"下载失败 HTTP {(int)resp.StatusCode}", (int)resp.StatusCode, (int)resp.StatusCode, null);

                    if (resp.StatusCode == (HttpStatusCode)206)
                    {
                        // 校验服务器从我们期望的偏移继续
                        var cr = resp.Content.Headers.ContentRange;
                        if (cr != null && cr.From != null && cr.From != downloaded)
                        {
                            _log.LogWarning("206 偏移不匹配（server={Off}, want={Want}），重下", cr.From, downloaded);
                            resp.Dispose();
                            downloaded = 0;
                            sha = FileUtil.CreateSha256();
                            fs.SetLength(0);
                            using var req3 = new HttpRequestMessage(HttpMethod.Get, new Uri(url));
                            resp = await _http.SendAsync(req3, HttpCompletionOption.ResponseHeadersRead, ct);
                        }
                    }

                    if (totalSize < 0)
                    {
                        if (resp.Content.Headers.ContentLength != null)
                            totalSize = (long)(resp.StatusCode == (HttpStatusCode)206
                                ? resp.Content.Headers.ContentLength!.Value + downloaded
                                : resp.Content.Headers.ContentLength!.Value);
                    }

                    await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                    int n;
                    while ((n = await stream.ReadAsync(buf, ct)) > 0)
                    {
                        fs.Write(buf, 0, n);
                        sha.TransformBlock(buf, 0, n, null, 0);
                        downloaded += n;

                        if ((DateTime.UtcNow - lastReport).TotalMilliseconds > 200)
                        {
                            progress?.Report(new DownloadProgress
                            {
                                Received = downloaded,
                                Total = totalSize,
                                SpeedBps = sw.ElapsedMilliseconds > 0 ? downloaded * 1000 / sw.ElapsedMilliseconds : 0,
                            });
                            lastReport = DateTime.UtcNow;
                        }
                    }
                    // 流结束（分片完成）。若还有剩余且支持 Range，循环外会再发 Range 请求
                    if (totalSize >= 0 && downloaded >= totalSize) break;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    // 网络抖动：落 meta 以便下次续传，然后重试
                    WriteMeta(meta, url, expectedSha256, downloaded, totalSize);
                    _log.LogWarning("下载中断（{Ex}），{Retry}s 后续传", ex.Message, 3);
                    await Task.Delay(TimeSpan.FromSeconds(3), ct);
                    continue;
                }
                finally
                {
                    resp?.Dispose();
                }
                // 防御：避免空转
                if (totalSize >= 0 && downloaded >= totalSize) break;
                if (downloaded == freshStart && freshStart > 0)
                {
                    // 206 但服务器返回 0 字节 → 重新完整下载
                    _log.LogWarning("服务器返回空分片，重新开始");
                    downloaded = 0;
                    sha = FileUtil.CreateSha256();
                    fs.SetLength(0);
                }
            }
        }

        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        var actual = FileUtil.Sha256FromHash(sha.Hash!);
        progress?.Report(new DownloadProgress
        {
            Received = downloaded,
            Total = totalSize,
            Verifying = true,
        });

        if (!string.IsNullOrWhiteSpace(expectedSha256) && actual != expectedSha256)
        {
            _log.LogError("sha256 校验失败：want {Want} got {Got}，删除 .part 重下", expectedSha256, actual);
            try { File.Delete(part); } catch { }
            try { File.Delete(meta); } catch { }
            return (false, part, $"sha256 校验失败（want {expectedSha256[..12]}… got {actual[..12]}…）");
        }

        if (File.Exists(destFile)) File.Delete(destFile);
        File.Move(part, destFile);
        try { File.Delete(meta); } catch { }
        _log.LogInformation("下载完成并校验通过：{Dest}（{Bytes} 字节）", destFile, downloaded);
        return (true, destFile, null);
    }

    /// <summary>若包为 zip/7z 则解压到 destDir，返回解压根目录；否则原样返回文件路径。</summary>
    public string MaterializePackage(string packagePath, string destDir)
    {
        var ext = Path.GetExtension(packagePath).ToLowerInvariant();
        if (ext is not (".zip" or ".7z" or ".rar" or ".tar"))
            return packagePath;

        FileUtil.EnsureDir(destDir);
        _log.LogInformation("解压存档包 {Pkg} → {Dest}", packagePath, destDir);
        try
        {
            // SharpCompress ≥0.41 将 ArchiveFactory.Open 重命名为 OpenArchive(path, ReaderOptions)。
            // 0.48.0 修复 GHSA-6c8g-7p36-r338（Zip Slip）。此处手动逐 entry 流式写出 + SafeJoin，
            // 不走 WriteToDirectory，本身已防路径遍历。
            using var archive = SharpCompress.Archives.ArchiveFactory.OpenArchive(packagePath, null);
            foreach (var entry in archive.Entries.Where(e => !e.IsDirectory))
            {
                var target = FileUtil.SafeJoin(destDir, entry.Key.Replace('\\', '/'));
                FileUtil.EnsureDir(Path.GetDirectoryName(target)!);
                var stream = File.Create(target);
                using (stream)
                {
                    entry.OpenEntryStream().CopyTo(stream);
                    stream.Flush();
                }
            }
            _log.LogInformation("解压完成：{Dest}", destDir);
            return destDir;
        }
        catch (Exception ex)
        {
            _log.LogError("解压失败（{Ex}），按原始包处理", ex.Message);
            return packagePath;
        }
    }

    private static void WriteMeta(string meta, string url, string sha, long downloaded, long total)
    {
        try
        {
            File.WriteAllText(meta, JsonSerializer.Serialize(new PartMeta
            {
                Url = url, Sha256 = sha, Downloaded = downloaded, Total = total
            }));
        }
        catch { /* ignore */ }
    }

    private sealed class PartMeta
    {
        public string? Url { get; set; }
        public string Sha256 { get; set; } = "";
        public long Downloaded { get; set; }
        public long Total { get; set; }
    }
}

public sealed class DownloadProgress
{
    public long Received { get; init; }
    public long Total { get; init; }
    public long SpeedBps { get; init; }
    public bool Verifying { get; init; }
    public double Percent => Total > 0 ? Received * 100.0 / Total : 0;
}

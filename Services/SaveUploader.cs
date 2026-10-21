using System.IO;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using VTC.Shared;
using VTC.Shared.Dtos;

namespace VTC.Client.Services;

/// <summary>
/// C4 存档上传（P1）：本地存档 → sha256 → upload-credential → PUT 直传 → publish。
/// 大文件不经过 API 走 body，API 只发"预签名/临时直传凭证 + 元数据"（规划 §2.1）。
/// </summary>
public sealed class SaveUploader
{
    private readonly IApiClient _api;
    private readonly ILogger<SaveUploader> _log;

    public SaveUploader(IApiClient api, ILogger<SaveUploader> log)
    {
        _api = api;
        _log = log;
    }

    public sealed class UploadResult
    {
        public bool Ok { get; init; }
        public string? Error { get; init; }
        public Guid? SaveId { get; init; }
        public string? Sha256 { get; init; }
        public long Size { get; init; }
    }

    /// <summary>
    /// 上传存档并直接发布。
    /// </summary>
    /// <param name="filePath">本地存档文件路径（zip / 原始 profile 文件夹已压缩）</param>
    /// <param name="label">存档标签（如"周末活动存档"）</param>
    /// <param name="game">ets2 | ats</param>
    /// <param name="gameVersion">游戏版本（如 1.61）</param>
    /// <param name="releaseNote">发布备注（可选）</param>
    public async Task<UploadResult> UploadAndPublishAsync(
        string filePath, string label, string game, string gameVersion,
        string? releaseNote = null,
        IProgress<string>? stage = null,
        CancellationToken ct = default)
    {
        if (!File.Exists(filePath))
            return Fail("文件不存在：" + filePath);

        var size = new FileInfo(filePath).Length;
        if (size == 0) return Fail("文件为空");

        // ① 计算 sha256
        stage?.Report("计算 SHA-256…");
        string sha256;
        try
        {
            sha256 = await Task.Run(() =>
            {
                using var fs = File.OpenRead(filePath);
                var hash = SHA256.HashData(fs);
                return Convert.ToHexString(hash).ToLowerInvariant();
            }, ct);
        }
        catch (Exception ex)
        {
            return Fail("计算 SHA-256 失败：" + ex.Message);
        }
        _log.LogInformation("SHA-256 完成：{File} = {Hash}", Path.GetFileName(filePath), sha256);

        // ② 获取上传凭证
        stage?.Report("获取上传凭证…");
        SaveCredentialDto cred;
        try
        {
            cred = await _api.PostAsync<SaveCredentialDto>(VtcApi.SaveUploadCredential,
                new { game, label, gameVersion, size, sha256 })
                ?? throw new Exception("upload-credential 返回空");
        }
        catch (Exception ex)
        {
            return Fail("获取上传凭证失败：" + ex.Message);
        }

        if (string.IsNullOrWhiteSpace(cred.UploadUrl))
            return Fail("uploadUrl 为空");

        // ③ PUT 直传
        stage?.Report("上传存档（" + FormatSize(size) + "）…");
        try
        {
            using var stream = File.OpenRead(filePath);
            var resp = await _api.PutStreamAsync(cred.UploadUrl, stream, size, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                return Fail($"直传失败 HTTP {(int)resp.StatusCode}：{Truncate(body, 300)}");
            }
        }
        catch (Exception ex)
        {
            return Fail("直传失败：" + ex.Message);
        }
        _log.LogInformation("直传完成：{Label}（{Size}）", label, FormatSize(size));

        // ④ 发布
        stage?.Report("发布存档…");
        try
        {
            // 服务端 publish 接口：POST /saves/{id}/publish
            // 但 upload-credential 响应没有 saveId（只有 saveKey），
            // 需要查列表找刚上传的存档 → 按 label+sha256 匹配
            var list = await _api.GetAsync<PageResult<SaveFileDto>>(VtcApi.Saves,
                new Dictionary<string, string?> { ["game"] = game, ["take"] = "5" });
            var save = list?.Items.FirstOrDefault(s => s.Sha256 == sha256)
                       ?? list?.Items.FirstOrDefault(s => s.Label == label);
            if (save == null)
                return new UploadResult { Ok = true, Sha256 = sha256, Size = size,
                    Error = "上传成功但未找到存档记录（可能尚未被服务端索引），请手动发布" };

            await _api.PostAsync<object>(
                VtcApi.SavePublish.Replace("{id}", save.Id.ToString()),
                new { note = releaseNote });
            _log.LogInformation("存档已发布：{Label}（{Id}）", label, save.Id);

            return new UploadResult { Ok = true, SaveId = save.Id, Sha256 = sha256, Size = size };
        }
        catch (Exception ex)
        {
            // 发布失败不致命：文件已上传，管理员可手动发布
            _log.LogWarning("自动发布失败（文件已上传，可手动发布）：{Ex}", ex.Message);
            return new UploadResult { Ok = true, Sha256 = sha256, Size = size,
                Error = "上传成功，自动发布失败：" + ex.Message + "（请手动发布）" };
        }
    }

    private static string FormatSize(long bytes) =>
        bytes >= 1073741824 ? $"{bytes / 1073741824.0:F1} GB" :
        bytes >= 1048576 ? $"{bytes / 1048576.0:F1} MB" :
        bytes >= 1024 ? $"{bytes / 1024.0:F1} KB" : $"{bytes} B";

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private static UploadResult Fail(string msg) => new() { Ok = false, Error = msg };
}

namespace VTC.Shared.Dtos;

/// <summary>已发布存档（GET /api/v1/saves）。</summary>
public class SaveFileDto
{
    public Guid Id { get; set; }
    public string Label { get; set; } = "";
    public string Game { get; set; } = Constants.GameETS2;
    public string GameVersion { get; set; } = "";
    public long Size { get; set; }
    public string Sha256 { get; set; } = "";
    public bool Published { get; set; }
    public DateTime? PublishedAt { get; set; }
    public int Downloads { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? DownloadUrl { get; set; }
}

/// <summary>
/// 直传凭证响应（POST /api/v1/saves/upload-credential）。
/// LocalStorage 下 uploadUrl 为本地短时效 URL（模拟预签名）；
/// S3 下为真正的预签名 PUT URL。
/// </summary>
public class SaveCredentialDto
{
    public string UploadUrl { get; set; } = "";
    public string SaveKey { get; set; } = "";
    public int ExpiresIn { get; set; } = Constants.PresignedSeconds;
}

/// <summary>下载凭证响应（POST /api/v1/saves/{id}/download-credential）。</summary>
public class SaveDownloadCredentialDto
{
    public string DownloadUrl { get; set; } = "";
    public int ExpiresIn { get; set; } = Constants.PresignedSeconds;
    public string Sha256 { get; set; } = "";
}

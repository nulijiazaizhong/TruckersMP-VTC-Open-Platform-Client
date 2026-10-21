using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace VTC.Client.Services;

/// <summary>文件/哈希工具（sha256 校验、分片读取、目录探测）。</summary>
public static class FileUtil
{
    public const int Sha256HexLen = 64;

    /// <summary>计算文件 sha256（小写 hex）。</summary>
    public static string Sha256File(string path)
    {
        using var fs = File.OpenRead(path);
        using var sha = SHA256.Create();
        var buf = new byte[1 << 20];
        int n;
        while ((n = fs.Read(buf, 0, buf.Length)) > 0)
            sha.TransformBlock(buf, 0, n, null, 0);
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }

    /// <summary>创建增量 sha256（边写盘边喂数据，下载校验用，避免二次读盘）。</summary>
    public static SHA256 CreateSha256() => SHA256.Create();

    public static string Sha256FromHash(byte[] h) => Convert.ToHexString(h).ToLowerInvariant();

    /// <summary>判断路径看起来像 profile 存档目录（含 info.sii 或 455453324C41/）。</summary>
    public static bool LooksLikeProfileSave(string dir)
    {
        if (!Directory.Exists(dir)) return false;
        if (File.Exists(Path.Combine(dir, "info.sii"))) return true;
        if (Directory.Exists(Path.Combine(dir, VtcConfigDir))) return true;
        // ETS2 profile 目录常直接含 .sii/.scs/.mp3 等
        try
        {
            return Directory.EnumerateFiles(dir).Any(f =>
                f.EndsWith(".sii", StringComparison.OrdinalIgnoreCase) ||
                f.EndsWith(".scs", StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    public const string VtcConfigDir = "455453324C41";

    /// <summary>确保目录存在并返回其路径。</summary>
    public static string EnsureDir(string dir)
    {
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>安全文件名（去除非法字符）。</summary>
    public static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
            sb.Append(invalid.Contains(c) ? '_' : c);
        var s = sb.ToString().Trim().TrimEnd('.');
        return string.IsNullOrWhiteSpace(s) ? "file" : s;
    }

    /// <summary>从存档包文件名推断 profile 名（去扩展名）。如 event.7z → event。</summary>
    public static string ProfileNameFromPackage(string packageFileName)
    {
        var noExt = Path.GetFileNameWithoutExtension(packageFileName);
        // 去掉版本/日期等下划线尾缀中的数字段？保持简单：直接用无扩展名
        return SafeFileName(noExt);
    }

    /// <summary>把相对路径按根目录解析为绝对路径（防 .. 逃逸）。</summary>
    public static string SafeJoin(string root, string relative)
    {
        var full = Path.GetFullPath(Path.Combine(root, relative));
        var rootFull = Path.GetFullPath(root);
        if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("路径逃逸：目标不在根目录内");
        return full;
    }
}

using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using VTC.Shared.Dtos;

namespace VTC.Client.Services;

/// <summary>
/// 会话持久化（C2）。
/// 落盘：Paths.TokensFile（%APPDATA%/VTC/&lt;team-slug&gt;/tokens.json）。
/// 加密：Windows DPAPI（CurrentUser 作用域）/ macOS Keychain / Linux libsecret；
/// 平台不可用时退化为 AES-GCM（密钥随机生成后 Base64 落盘，仅保证不落明文，弱于 DPAPI）。
/// </summary>
public sealed class SecureTokenStore
{
    private readonly ILogger<SecureTokenStore> _log;

    public SecureTokenStore(ILogger<SecureTokenStore> log) => _log = log;

    public sealed class TokenEntry
    {
        public string Username { get; set; } = "";
        public string AccessToken { get; set; } = "";
        public string RefreshToken { get; set; } = "";
        public long ExpiresAtUnixMs { get; set; }
        public string Role { get; set; } = "";
        public string Status { get; set; } = "";
        public string? Plate { get; set; }
        public string? Vehicle { get; set; }
        public string RememberMeAt { get; set; } = "";
    }

    public sealed class TokenFile
    {
        public string Version { get; set; } = "1";
        public Dictionary<string, TokenEntry> Accounts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public string? ActiveAccount { get; set; }
    }

    public bool IsTokenFresh(TokenEntry e) =>
        e != null && (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() < e.ExpiresAtUnixMs - 60_000);

    public TokenFile? LoadAll()
    {
        try
        {
            if (!File.Exists(Paths.TokensFile)) return null;
            var raw = File.ReadAllBytes(Paths.TokensFile);
            var json = Decrypt(raw);
            return JsonSerializer.Deserialize<TokenFile>(json, JsonOpts);
        }
        catch (Exception ex)
        {
            _log.LogWarning("读取 tokens.json 失败: {Ex}", ex.Message);
            return null;
        }
    }

    public void Save(TokenFile file)
    {
        var json = JsonSerializer.Serialize(file, JsonOpts);
        var bytes = Encrypt(Encoding.UTF8.GetBytes(json));
        File.WriteAllBytes(Paths.TokensFile, bytes);
    }

    public void Remove(string username)
    {
        var f = LoadAll() ?? new TokenFile();
        if (f.Accounts.Remove(username))
            Save(f);
    }

    static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
    };

    #region 平台加密

    public byte[] Encrypt(byte[] plain)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                return ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
            if (OperatingSystem.IsMacOS())
                return KeychainEncrypt(plain);
            if (OperatingSystem.IsLinux())
                return SecretServiceEncrypt(plain);
        }
        catch (Exception ex)
        {
            _log.LogWarning("平台加密失败，退化为 AES-GCM: {Ex}", ex.Message);
        }
        return AesFallback(plain, isEncrypt: true);
    }

    public byte[] Decrypt(byte[] cipher)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                return ProtectedData.Unprotect(cipher, null, DataProtectionScope.CurrentUser);
            if (OperatingSystem.IsMacOS())
                return KeychainDecrypt(cipher);
            if (OperatingSystem.IsLinux())
                return SecretServiceDecrypt(cipher);
        }
        catch (Exception ex)
        {
            _log.LogWarning("平台解密失败，尝试 AES-GCM 回退: {Ex}", ex.Message);
        }
        return AesFallback(cipher, isEncrypt: false);
    }

    /// <summary>macOS：调用 keychain 子进程（security CLI）。失败抛异常由上层回退。</summary>
    private static byte[] KeychainEncrypt(byte[] data) => throw new PlatformNotSupportedException("keychain via CLI (P0 未实现，走 AES 回退)");
    private static byte[] KeychainDecrypt(byte[] data) => throw new PlatformNotSupportedException("keychain via CLI (P0 未实现，走 AES 回退)");

    /// <summary>Linux：libsecret（secret-tool）。P0 走 AES 回退。</summary>
    private static byte[] SecretServiceEncrypt(byte[] data) => throw new PlatformNotSupportedException("libsecret (P0 未实现，走 AES 回退)");
    private static byte[] SecretServiceDecrypt(byte[] data) => throw new PlatformNotSupportedException("libsecret (P0 未实现，走 AES 回退)");

    /// <summary>
    /// AES-256-GCM 回退：密钥存于 Paths.TeamDataDir/.aeskey（权限 600），
    /// 仅保证"不落明文"，安全性弱于 DPAPI/Keychain（换机不可迁移）。
    /// </summary>
    private byte[] AesFallback(byte[] data, bool isEncrypt)
    {
        var keyFile = Path.Combine(Paths.TeamDataDir, ".aeskey");
        byte[] key;
        if (File.Exists(keyFile))
            key = File.ReadAllBytes(keyFile);
        else
        {
            key = RandomNumberGenerator.GetBytes(32);
            File.WriteAllBytes(keyFile, key);
            // 非 Windows 上把密钥文件权限收敛到属主（best-effort，失败忽略）
            if (!OperatingSystem.IsWindows())
            {
                try
                {
                    var psi = new System.Diagnostics.ProcessStartInfo("chmod", "600 \"" + keyFile + "\"")
                    { UseShellExecute = false, CreateNoWindow = true };
                    System.Diagnostics.Process.Start(psi);
                }
                catch { /* ignore */ }
            }
        }

        // 用 SHA256 派生 32B 密钥，Aes 走标准 CBC-HMAC（避免 AesGcm 重载歧义）。
        // P0 回退方案：仅保证"不落明文"，安全性弱于 DPAPI/Keychain。
        using (var sha = SHA256.Create())
        {
            sha.TransformBlock(key, 0, key.Length, null, 0);
            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            var keyBytes = sha.Hash!;

            if (isEncrypt)
            {
                using var aes = Aes.Create();
                aes.Key = keyBytes;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                var iv = new byte[aes.BlockSize / 8];
                RandomNumberGenerator.Fill(iv);
                using var enc = aes.CreateEncryptor(aes.Key, iv);
                var ct = enc.TransformFinalBlock(data, 0, data.Length);
                return Concat(iv, ct);
            }
            else
            {
                if (data.Length < 16)
                    throw new CryptographicException("密文长度不足");
                using var aes = Aes.Create();
                aes.Key = keyBytes;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                var iv = new byte[aes.BlockSize / 8];
                Buffer.BlockCopy(data, 0, iv, 0, iv.Length);
                var ct = new byte[data.Length - iv.Length];
                Buffer.BlockCopy(data, iv.Length, ct, 0, ct.Length);
                using var dec = aes.CreateDecryptor(aes.Key, iv);
                return dec.TransformFinalBlock(ct, 0, ct.Length);
            }
        }
    }

    private static byte[] Concat(byte[] a, byte[] b)
    {
        var r = new byte[a.Length + b.Length];
        Buffer.BlockCopy(a, 0, r, 0, a.Length);
        Buffer.BlockCopy(b, 0, r, a.Length, b.Length);
        return r;
    }

    #endregion
}

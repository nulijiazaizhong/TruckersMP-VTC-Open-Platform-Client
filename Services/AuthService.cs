using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using VTC.Shared;
using VTC.Shared.Dtos;

namespace VTC.Client.Services;

/// <summary>
/// 认证 / 会话（C2）。
/// - 账号密码 → JWT（GET/POST /api/v1/auth/login）；
/// - 记住我：勾选后 token 持久化（DPAPI 加密，Paths.TokensFile）；
/// - 多账号切换：登录页顶部下拉，切换已登录账号；
/// - 会话持久化：%APPDATA%/VTC/&lt;team-slug&gt;/tokens.json；
/// - access token 过期 → /auth/refresh 轮换；
/// - 可选 TM:MP / Steam 身份绑定（P0 仅 UI 预留，不强制校验，§2.3/契约）。
/// </summary>
public sealed class AuthService : IAuthService
{
    private readonly IApiClient _api;
    private readonly SecureTokenStore _store;
    private readonly IConfiguration _cfg;
    private readonly ILogger<AuthService> _log;

    public string? AccessToken { get; private set; }
    public string? RefreshToken { get; private set; }
    public bool IsAuthenticated => !string.IsNullOrWhiteSpace(AccessToken);
    public string? CurrentUsername { get; private set; }
    public string? CurrentRole { get; private set; }
    public string? CurrentStatus { get; private set; }
    public string? Plate { get; private set; }
    public string? Vehicle { get; private set; }
    public DateTime? ExpiresAtUtc { get; private set; }
    public string? TmpPlayerId { get; set; }
    public string? SteamId { get; set; }

    /// <summary>已记住的账号列表（供登录页下拉切换）。</summary>
    public IReadOnlyList<SecureTokenStore.TokenEntry> RememberedAccounts { get; private set; } = Array.Empty<SecureTokenStore.TokenEntry>();

    public event EventHandler? AuthChanged;

    public AuthService(IApiClient api, SecureTokenStore store, IConfiguration cfg, ILogger<AuthService> log)
    {
        _api = api;
        _store = store;
        _cfg = cfg;
        _log = log;
    }

    /// <summary>启动时从本地恢复：取活动账号（优先 token 仍新鲜，其次最近记住）。</summary>
    public SecureTokenStore.TokenEntry? RestoreSession()
    {
        var file = _store.LoadAll();
        if (file == null || file.Accounts.Count == 0) return null;

        RememberedAccounts = file.Accounts.Values
            .OrderByDescending(e => DateTime.TryParse(e.RememberMeAt, out var t) ? t : DateTime.MinValue)
            .ToList();

        var active = file.ActiveAccount != null && file.Accounts.TryGetValue(file.ActiveAccount, out var a) ? a : null;
        var fresh = RememberedAccounts.FirstOrDefault(_store.IsTokenFresh)
                    ?? RememberedAccounts.FirstOrDefault();
        var pick = (active != null && _store.IsTokenFresh(active)) ? active : fresh;
        if (pick == null) return null;

        ApplySession(pick, persist: false, setActive: true);
        _log.LogInformation("已恢复本地会话：{User}（role={Role}）", pick.Username, pick.Role);
        return pick;
    }

    /// <summary>账号密码登录。rememberMe=true 才落盘持久化。</summary>
    public async Task<AuthLoginResultDto> LoginAsync(string username, string password, bool rememberMe,
        string? tmpPlayerId = null, string? steamId = null)
    {
        TmpPlayerId = tmpPlayerId; SteamId = steamId;
        var body = new AuthLoginDto
        {
            Username = username,
            Password = password,
            RememberMe = rememberMe,
            TmpPlayerId = string.IsNullOrWhiteSpace(tmpPlayerId) ? null : tmpPlayerId,
            SteamId = string.IsNullOrWhiteSpace(steamId) ? null : steamId,
        };

        var result = await _api.PostAsync<AuthLoginResultDto>(VtcApi.AuthLogin, body, useAuth: false);
        if (result == null || string.IsNullOrWhiteSpace(result.AccessToken))
            throw new ApiException("登录响应缺少 accessToken", ErrorCodes.BizBadCredentials, 401, null);

        if (result.User != null)
        {
            if (result.User.Status.Equals(Constants.StatusDisabled, StringComparison.OrdinalIgnoreCase))
                throw new ApiException("账号已被禁用，请联系车队管理员", ErrorCodes.BizUserDisabled, 403, null);
        }

        var entry = new SecureTokenStore.TokenEntry
        {
            Username = username,
            AccessToken = result.AccessToken,
            RefreshToken = result.RefreshToken ?? "",
            ExpiresAtUnixMs = DateTimeOffset.UtcNow.AddSeconds(result.ExpiresIn > 0 ? result.ExpiresIn : Constants.AccessTokenSeconds).ToUnixTimeMilliseconds(),
            Role = result.User?.Role ?? "",
            Status = result.User?.Status ?? "",
            Plate = result.Member?.Plate,
            Vehicle = result.Member?.Vehicle,
            RememberMeAt = rememberMe ? DateTime.Now.ToString("O") : "",
        };

        ApplySession(entry, persist: rememberMe, setActive: true);

        if (rememberMe)
        {
            var file = _store.LoadAll() ?? new SecureTokenStore.TokenFile();
            file.Accounts[username] = entry;
            file.ActiveAccount = username;
            _store.Save(file);
        }

        RefreshRemembered();
        _log.LogInformation("登录成功：{User} role={Role} remember={RM}", username, entry.Role, rememberMe);
        return result;
    }

    /// <summary>切换已记住账号（多账号切换）。优先复用本地 token，过期则刷新。</summary>
    public async Task<AuthLoginResultDto?> SwitchAccountAsync(string username)
    {
        var file = _store.LoadAll();
        if (file == null || !file.Accounts.TryGetValue(username, out var entry))
            throw new ApiException($"未找到已记住的账号：{username}", ErrorCodes.BizUserNotFound, 404, null);

        if (!_store.IsTokenFresh(entry))
        {
            if (string.IsNullOrWhiteSpace(entry.RefreshToken))
                throw new ApiException($"账号 {username} 会话已过期且无刷新令牌，请重新登录", ErrorCodes.BizRefreshInvalid, 401, null);
            var refreshed = await RefreshAsync(entry.RefreshToken);
            if (refreshed != null)
            {
                entry.AccessToken = refreshed.AccessToken;
                entry.RefreshToken = refreshed.RefreshToken;
                entry.ExpiresAtUnixMs = DateTimeOffset.UtcNow.AddSeconds(refreshed.ExpiresIn).ToUnixTimeMilliseconds();
            }
        }

        ApplySession(entry, persist: true, setActive: true);
        RefreshRemembered();
        return null; // 调用方以当前会话为准
    }

    public void Logout()
    {
        AccessToken = null;
        RefreshToken = null;
        CurrentUsername = null;
        CurrentRole = null;
        Plate = null; Vehicle = null;
        ExpiresAtUtc = null;
        _log.LogInformation("已登出：{User}", CurrentUsername);
        AuthChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>移除某个记住的账号（清空其本地 token）。</summary>
    public void ForgetAccount(string username)
    {
        _store.Remove(username);
        if (CurrentUsername == username) Logout();
        RefreshRemembered();
    }

    private void ApplySession(SecureTokenStore.TokenEntry e, bool persist, bool setActive)
    {
        AccessToken = e.AccessToken;
        RefreshToken = e.RefreshToken;
        CurrentUsername = e.Username;
        CurrentRole = e.Role;
        CurrentStatus = e.Status;
        Plate = e.Plate;
        Vehicle = e.Vehicle;
        if (long.TryParse(e.ExpiresAtUnixMs.ToString(), out var ms))
            ExpiresAtUtc = DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
        if (persist)
        {
            var file = _store.LoadAll() ?? new SecureTokenStore.TokenFile();
            file.Accounts[e.Username] = e;
            if (setActive) file.ActiveAccount = e.Username;
            _store.Save(file);
        }
        else if (setActive)
        {
            var file = _store.LoadAll();
            if (file != null && file.Accounts.TryGetValue(e.Username, out var exist))
            {
                exist.AccessToken = e.AccessToken;
                exist.RefreshToken = e.RefreshToken;
                file.Accounts[e.Username] = exist;
                file.ActiveAccount = e.Username;
                _store.Save(file);
            }
        }
        AuthChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RefreshRemembered()
    {
        var file = _store.LoadAll();
        var list = file?.Accounts.Values
            .OrderByDescending(e => DateTime.TryParse(e.RememberMeAt, out var t) ? t : DateTime.MinValue)
            .ToList();
        RememberedAccounts = list ?? new List<SecureTokenStore.TokenEntry>();
    }

    #region Refresh

    /// <summary>
    /// IAuthService.TryRefreshAsync（供 ApiClient 401 重试）。
    /// 必须异步：此前 .GetAwaiter().GetResult() 在 UI 线程同步阻塞，
    /// 而 RefreshAsync 的 HTTP 完成回调需回 UI 线程 → 死锁（个人中心卡死根因）。
    /// </summary>
    public async Task<string?> TryRefreshAsync()
    {
        if (string.IsNullOrWhiteSpace(RefreshToken)) return null;
        try
        {
            var result = await RefreshAsync(RefreshToken);
            if (result != null && !string.IsNullOrWhiteSpace(result.AccessToken))
                return result.AccessToken;
            // 刷新返回空 = refresh token 已作废（登出/轮换/过期）：自动登出，
            // 避免继续用坏 token 反复 401；AuthChanged 会让 Shell 跳回登录页。
            _log.LogWarning("刷新令牌无效（refresh 返回空），自动登出请重新登录");
            Logout();
            return null;
        }
        catch (Exception ex)
        {
            _log.LogWarning("刷新令牌失败：{Ex}", ex.Message);
            return null;
        }
    }

    public async Task<AuthLoginResultDto?> RefreshAsync(string refreshToken)
    {
        try
        {
            var body = new AuthRefreshDto { RefreshToken = refreshToken };
            var result = await _api.PostAsync<AuthLoginResultDto>(VtcApi.AuthRefresh, body, useAuth: false);
            if (result == null || string.IsNullOrWhiteSpace(result.AccessToken)) return null;
            AccessToken = result.AccessToken;
            RefreshToken = string.IsNullOrWhiteSpace(result.RefreshToken) ? RefreshToken : result.RefreshToken;
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(result.ExpiresIn).UtcDateTime;
            // 持久化新 token（轮换）
            if (CurrentUsername != null)
            {
                var file = _store.LoadAll();
                if (file?.Accounts.TryGetValue(CurrentUsername, out var e) == true)
                {
                    e.AccessToken = AccessToken;
                    e.RefreshToken = RefreshToken ?? "";
                    e.ExpiresAtUnixMs = DateTimeOffset.UtcNow.AddSeconds(result.ExpiresIn).ToUnixTimeMilliseconds();
                    _store.Save(file);
                }
            }
            _log.LogInformation("token 刷新成功（轮换）");
            return result;
        }
        catch (Exception ex)
        {
            _log.LogWarning("auth/refresh 失败：{Ex}", ex.Message);
            return null;
        }
    }

    #endregion
}

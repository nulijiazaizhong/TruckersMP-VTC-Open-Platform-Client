using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VTC.Client.Services;
using VTC.Shared.Dtos;

namespace VTC.Client.ViewModels;

/// <summary>登录视图模型：账号密码 → JWT，记住我 / 多账号切换 / 身份绑定预留。</summary>
public partial class LoginViewModel : ObservableObject
{
    private readonly AuthService _auth;
    private readonly BrandingService _branding;

    [ObservableProperty] private string _username = "";
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private bool _rememberMe = true;
    [ObservableProperty] private string _tmpPlayerId = "";
    [ObservableProperty] private string _errorText = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private Bitmap? _backgroundImage;
    [ObservableProperty] private Bitmap? _logo;
    [ObservableProperty] private string _layout = "left";   // left | right | center
    [ObservableProperty] private string _teamName = "";
    [ObservableProperty] private string _teamSlogan = "";

    /// <summary>已记住账号（多账号切换）。</summary>
    [ObservableProperty] private IReadOnlyList<SecureTokenStore.TokenEntry> _accounts =
        System.Array.Empty<SecureTokenStore.TokenEntry>();
    [ObservableProperty] private SecureTokenStore.TokenEntry? _selectedAccount;

    public bool IsAuthenticated => _auth.IsAuthenticated;

    public LoginViewModel(AuthService auth, BrandingService branding)
    {
        _auth = auth;
        _branding = branding;
        TeamName = _branding.Current.TeamName;
        TeamSlogan = _branding.Current.TeamSlogan;
        Layout = _branding.Current.LoginLayout;
        Accounts = _auth.RememberedAccounts;
        // 默认选中活动账号（若有）
        if (_auth.CurrentUsername != null)
            SelectedAccount = Accounts.FirstOrDefault(a => a.Username == _auth.CurrentUsername);
    }

    public void RefreshBrand()
    {
        TeamName = _branding.Current.TeamName;
        TeamSlogan = _branding.Current.TeamSlogan;
        Layout = _branding.Current.LoginLayout;
        Accounts = _auth.RememberedAccounts;
    }

    public async Task LoadBackgroundAsync()
    {
        var cfg = _branding.Current;
        BackgroundImage = await _branding.ResolveBrandImageAsync(cfg.LoginBg,
            "pack://app-assets/brand/login_bg.jpg", isLogo: false);
        Logo = await _branding.ResolveBrandImageAsync(cfg.TeamLogo,
            "pack://app-assets/brand/logo.png", isLogo: true);
    }

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task LoginAsync()
    {
        ErrorText = "";
        IsBusy = true;
        try
        {
            if (string.IsNullOrWhiteSpace(Username) || string.IsNullOrWhiteSpace(Password))
            {
                ErrorText = "请输入用户名和密码";
                return;
            }

            await _auth.LoginAsync(Username, Password, RememberMe,
                string.IsNullOrWhiteSpace(TmpPlayerId) ? null : TmpPlayerId);

            // 登录成功：主框架通过 AuthChanged 切换；这里同步一次
        }
        catch (Services.ApiException ex)
        {
            ErrorText = MapError(ex);
        }
        catch (Exception ex)
        {
            ErrorText = "登录失败：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void SwitchAccount(SecureTokenStore.TokenEntry? account)
    {
        if (account == null) return;
        // 切换已记住账号（复用本地 token / 刷新）
        _ = Task.Run(async () =>
        {
            try
            {
                await _auth.SwitchAccountAsync(account.Username);
                Username = account.Username;
            }
            catch (Exception ex)
            {
                ErrorText = "切换账号失败：" + ex.Message;
            }
        });
    }

    private string MapError(Services.ApiException ex) => ex.Code switch
    {
        1002 or 401 => "用户名或密码错误",
        1003 or 403 => "账号已被禁用，请联系车队管理员",
        1001 => "用户不存在",
        1005 => "请求过于频繁，请稍后再试",
        _ => string.IsNullOrWhiteSpace(ex.Message) ? "登录失败" : ex.Message,
    };

    private bool NotBusy() => !IsBusy;
}

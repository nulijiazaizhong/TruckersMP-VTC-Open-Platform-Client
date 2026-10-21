using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using VTC.Client.Services;
using VTC.Shared.Dtos;

namespace VTC.Client.ViewModels;

/// <summary>
/// 主框架视图模型：导航高亮、品牌（车队名/logo/slogan）、登录态、Admin 可见性。
/// </summary>
public partial class ShellViewModel : ObservableObject
{
    private readonly AuthService _auth;
    private readonly BrandingService _branding;

    [ObservableProperty] private bool _isAuthenticated;
    [ObservableProperty] private bool _isAdmin;
    [ObservableProperty] private string _activeNav = "home";
    [ObservableProperty] private string _teamName = "VTC 车队";
    [ObservableProperty] private string _teamSlogan = "";
    [ObservableProperty] private Bitmap? _brandLogo;

    public ShellViewModel(AuthService auth, BrandingService branding)
    {
        _auth = auth;
        _branding = branding;
        RefreshAuthState();
        _auth.AuthChanged += (_, _) =>
        {
            try { Avalonia.Threading.Dispatcher.UIThread.Post(() => RefreshAuthState()); }
            catch { /* ignore */ }
        };
    }

    public void RefreshAuthState()
    {
        IsAuthenticated = _auth.IsAuthenticated;
        IsAdmin = _auth.CurrentRole is VTC.Shared.Constants.RoleAdmin or VTC.Shared.Constants.RoleManager;
    }
}

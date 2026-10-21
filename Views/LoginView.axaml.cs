using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using VTC.Client.Services;
using VTC.Client.ViewModels;

namespace VTC.Client.Views;

public partial class LoginView : UserControl
{
    private LoginViewModel _vm = null!;
    private BrandingService _branding = null!;

    public LoginView()
    {
        InitializeComponent();
        _branding = AppServiceLocator.Get<BrandingService>();
        var auth = AppServiceLocator.Get<AuthService>();
        _vm = new LoginViewModel(auth, _branding);
        Initialize(_vm);
        // 异步加载品牌图 + 应用版式
        _ = ApplyBrandAsync();
        // 密码框用 FindControl 查找（x:Name 生成器对 PasswordBox 不稳定）
        _pwd = this.FindControl<Avalonia.Controls.TextBox>("pwd");
        if (_pwd != null)
        {
            _pwd.LostFocus += (_, _) => _vm.Password = _pwd.Text;
            _vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(LoginViewModel.Password) && _pwd != null)
                    _pwd.Text = _vm.Password;
            };
        }
    }

    private Avalonia.Controls.TextBox? _pwd;


    private void Initialize(LoginViewModel vm)
    {
        // vm → 控件（单向 + 事件）
        vm.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(LoginViewModel.RememberMe):
                    remember.IsChecked = vm.RememberMe;
                    break;
                case nameof(LoginViewModel.ErrorText):
                    errorText.Text = vm.ErrorText;
                    break;
                case nameof(LoginViewModel.IsBusy):
                    loginBtn.IsEnabled = !vm.IsBusy;
                    loginProgress.IsVisible = vm.IsBusy;
                    break;
                case nameof(LoginViewModel.Layout):
                case nameof(LoginViewModel.BackgroundImage):
                    ApplyLayout(vm.Layout, vm.BackgroundImage);
                    break;
            }
        };

        // 控件 → vm（输入事件；密码框在构造函数里用 FindControl 绑）
        username.LostFocus += (_, _) => vm.Username = username.Text;
        tmpId.LostFocus += (_, _) => vm.TmpPlayerId = tmpId.Text;
        remember.IsCheckedChanged += (_, _) => vm.RememberMe = remember.IsChecked == true;

        // 登录点击
        loginBtn.Click += async (_, _) => await vm.LoginCommand.ExecuteAsync(null);
    }

    private async Task ApplyBrandAsync()
    {
        await _vm.LoadBackgroundAsync();
        // 车队名 / slogan / logo 用命名元素直绑（已绑定 Text，这里补 logo 与刷新）
        teamName.Text = _vm.TeamName;
        slogan.Text = _vm.TeamSlogan;
        if (_vm.Logo != null) logo.Source = _vm.Logo;
        RenderAccountList();
        ApplyLayout(_vm.Layout, _vm.BackgroundImage);
    }

    private void RenderAccountList()
    {
        var accounts = _vm.Accounts;
        if (accounts == null || accounts.Count == 0)
        {
            accountPanel.IsVisible = false;
            return;
        }
        accountPanel.IsVisible = true;
        accountList.Children.Clear();
        foreach (var acc in accounts.Take(6))
        {
            var b = new Button
            {
                Content = acc.Username,
                Classes = { "account-btn" },
                Tag = acc,
                Margin = new Thickness(0, 0, 8, 0),
                Foreground = _vm.SelectedAccount?.Username == acc.Username
                    ? new SolidColorBrush(Color.FromRgb(30, 136, 229))
                    : Brushes.Gray,
            };
            b.Click += (_, _) => _vm.SwitchAccountCommand.Execute(acc);
            accountList.Children.Add(b);
        }
    }

    /// <summary>
    /// 按 LOGIN_LAYOUT 应用三种版式（§2.3）：
    /// left = 左表单 + 右大图；right = 左大图 + 右表单；center = 整屏背景 + 中央半透明卡片。
    /// 实现：背景图铺满整窗（有图时），表单容器按版式定位（左/右/中），分栏时加纯色底保证可读。
    /// </summary>
    private void ApplyLayout(string layout, Avalonia.Media.Imaging.Bitmap? bg)
    {
        layout = (layout ?? "left").ToLowerInvariant();

        if (bg != null)
        {
            bgImage.Source = bg;
            bgImage.IsVisible = true;
        }
        else
        {
            bgImage.IsVisible = false;
            this.Background = new SolidColorBrush(Color.FromRgb(16, 20, 24));
        }

        formContainer.Width = 380;
        formContainer.VerticalAlignment = VerticalAlignment.Center;

        switch (layout)
        {
            case "center":
                formContainer.HorizontalAlignment = HorizontalAlignment.Center;
                formContainer.Margin = new Thickness(0);
                // 深色半透明卡片（比背景 #101418 略亮）。此前半透明白(230,255,255,255)在深色主题下刺眼。
                formContainer.Background = new SolidColorBrush(Color.FromArgb(235, 30, 34, 40));
                formContainer.BorderBrush = new SolidColorBrush(Color.FromRgb(46, 52, 64));
                formContainer.BorderThickness = new Thickness(1);
                formContainer.CornerRadius = new CornerRadius(12);
                break;

            case "right":
                // 左大图、右表单
                formContainer.HorizontalAlignment = HorizontalAlignment.Right;
                formContainer.Margin = new Thickness(0, 0, 40, 0);
                formContainer.Background = new SolidColorBrush(Color.FromRgb(30, 34, 40));
                formContainer.CornerRadius = new CornerRadius(0);
                break;

            case "left":
            default:
                // 左表单、右大图
                formContainer.HorizontalAlignment = HorizontalAlignment.Left;
                formContainer.Margin = new Thickness(40, 0, 0, 0);
                // 深色底（与背景 #101418 略不同色以区分布局）。此前 Brushes.White 纯白在深色主题下刺眼。
                formContainer.Background = new SolidColorBrush(Color.FromRgb(24, 28, 34));
                formContainer.CornerRadius = new CornerRadius(0);
                break;
        }
    }

    public void Refresh()
    {
        _vm.RefreshBrand();
        teamName.Text = _vm.TeamName;
        slogan.Text = _vm.TeamSlogan;
        RenderAccountList();
    }
}

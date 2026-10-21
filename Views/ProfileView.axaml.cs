using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using VTC.Client.Services;

namespace VTC.Client.Views;

/// <summary>个人中心（P1 占位 + 当前登录信息）：成员信息 / 我的活动记录 / 我的积分排名。</summary>
public partial class ProfileView : UserControl
{
    public ProfileView()
    {
        InitializeComponent();
        var sp = AppServiceLocator.Services;
        var auth = sp.GetRequiredService<AuthService>();
        var settings = sp.GetRequiredService<AppSettings>();

        fUsername.Text = auth.CurrentUsername ?? "—";
        fRole.Text = auth.CurrentRole ?? "—";
        fPlate.Text = string.IsNullOrWhiteSpace(auth.Plate) ? "—" : auth.Plate;
        fVehicle.Text = string.IsNullOrWhiteSpace(auth.Vehicle) ? "—" : auth.Vehicle;
        fStatus.Text = auth.CurrentStatus ?? "—";

        activeText.Text = string.IsNullOrWhiteSpace(settings.ActiveEventProfile)
            ? "（无已部署活动存档）"
            : $"profile：{settings.ActiveEventProfile}\nDLC 备份：{settings.ActiveDlcStashDir ?? "—"}";

        recordsEmpty.IsVisible = true; // P0 无记录数据源
    }

}

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace VTC.Client.Views;

/// <summary>
/// 添加成员成功弹窗：随机密码模式下展示一次性初始密码并支持一键复制；
/// 管理员自填密码时传 null，仅显示成功结果（密码已知无需展示）。
/// </summary>
public partial class InitialPasswordDialog : Window
{
    private readonly string? _password;

    public InitialPasswordDialog()
    {
        InitializeComponent();
    }

    public InitialPasswordDialog(string username, string role, string? password) : this()
    {
        _password = password;
        titleText.Text = $"已添加 {username}（{role}）";
        if (password == null)
        {
            subText.Text = "成员已创建，可使用你设定的密码登录。";
            pwdPanel.IsVisible = false;
            btnCopy.IsVisible = false;
        }
        else
        {
            pwdBox.Text = password;
        }
    }

    private async void OnCopyClick(object? sender, RoutedEventArgs e)
    {
        if (_password == null) return;
        // 无剪贴板环境（部分 Linux/Wayland）下 Clipboard 为 null，静默降级为手动拖选
        if (Clipboard != null) await Clipboard.SetTextAsync(_password);
        btnCopy.Content = "✓ 已复制";
        await Task.Delay(1200);
        btnCopy.Content = "复制初始密码";
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
        base.OnKeyDown(e);
    }
}

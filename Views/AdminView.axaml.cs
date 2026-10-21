using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace VTC.Client.Views;

/// <summary>管理页（C13，P1）。P0：界面占位 + 表单骨架，不实现完整 CRUD（按任务"只做 P0"约束）。</summary>
public partial class AdminView : UserControl
{
    public AdminView() => InitializeComponent();


    private void OnSaveEventClick(object? sender, RoutedEventArgs e)
    {
        // P1：POST /api/v1/events（Admin/Manager）。P0 仅占位提示。
        evtStatus.Text = "P1 功能：活动创建/更新（POST /api/v1/events）尚未在 P0 实现";
    }

    private void OnSaveServerClick(object? sender, RoutedEventArgs e)
    {
        serverStatus.Text = "P1 功能：服务端存储配置（凭据存后端 .env）尚未在 P0 实现";
    }

    private void OnUploadImageClick(object? sender, RoutedEventArgs e)
        => imgHint.Text = "P1：活动图直传对象存储（预签名 PUT）尚未在 P0 实现";

    private void OnUploadSaveClick(object? sender, RoutedEventArgs e)
        => saveHint.Text = "P1：存档直传 + 发布（upload-credential + publish）尚未在 P0 实现";
}

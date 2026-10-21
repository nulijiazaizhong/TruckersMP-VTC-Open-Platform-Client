using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Microsoft.Extensions.DependencyInjection;
using VTC.Client.Services;
using VTC.Shared;
using VTC.Shared.Dtos;

namespace VTC.Client.Views;

/// <summary>
/// 管理页（C13 P1 实装）：活动 CRUD + 存档上传（C4）+ 服务端设置提示。
/// </summary>
public partial class AdminView : UserControl
{
    private IApiClient? _api;
    private SaveUploader? _uploader;
    private Guid? _editingEventId;

    public AdminView()
    {
        InitializeComponent();
        var sp = AppServiceLocator.Services;
        _api = sp?.GetService<IApiClient>();
        _uploader = sp?.GetService<SaveUploader>();
    }

    // ---------- 活动 CRUD（C13） ----------

    private async void OnSaveEventClick(object? sender, RoutedEventArgs e)
    {
        if (_api == null) { evtStatus.Text = "API 不可用"; return; }

        var name = evtName.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(name))
        {
            evtStatus.Text = "活动标题不能为空";
            evtStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xF4, 0x43, 0x36));
            return;
        }
        evtStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x1B, 0x7A, 0x43));

        try
        {
            DateTime? ParseDt(string? s) =>
                DateTime.TryParse(s?.Trim(), out var d) ? d : null;

            object body;
            string path;
            HttpMethod method;

            if (_editingEventId != null)
            {
                // PATCH（更新）
                body = new
                {
                    name,
                    activityAt = ParseDt(evtActivityAt.Text),
                    departAt = ParseDt(evtDepartAt.Text),
                    server = evtServer.Text?.Trim(),
                    navigation = evtNav.Text?.Trim(),
                    requiresMapDlc = string.IsNullOrWhiteSpace(evtRequiresMapDlc.Text) ? null : evtRequiresMapDlc.Text.Trim(),
                };
                path = VtcApi.EventDetail.Replace("{id}", _editingEventId.Value.ToString());
                method = new HttpMethod("PATCH");
            }
            else
            {
                // POST（创建）
                body = new
                {
                    name,
                    etype = "activity",
                    activityAt = ParseDt(evtActivityAt.Text) ?? DateTime.UtcNow.AddDays(1),
                    departAt = ParseDt(evtDepartAt.Text),
                    server = evtServer.Text?.Trim() ?? "",
                    navigation = evtNav.Text?.Trim() ?? "跟随头车",
                    status = "active",
                    requiresMapDlc = string.IsNullOrWhiteSpace(evtRequiresMapDlc.Text) ? null : evtRequiresMapDlc.Text.Trim(),
                };
                path = VtcApi.Events;
                method = HttpMethod.Post;
            }

            var resp = await SendJsonAsync(method, path, body);
            if (resp != null)
            {
                var evtId = ExtractGuid(resp, "id");
                if (evtId != null) _editingEventId = evtId;
                evtStatus.Text = _editingEventId == null
                    ? $"活动已创建：{name}"
                    : $"活动已更新：{name}（{evtId:R}）";
            }
        }
        catch (Exception ex)
        {
            evtStatus.Text = "保存失败：" + ex.Message;
            evtStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xF4, 0x43, 0x36));
        }
    }

    // ---------- 存档上传（C4） ----------

    private async void OnUploadSaveClick(object? sender, RoutedEventArgs e)
    {
        if (_uploader == null) { saveHint.Text = "上传服务不可用"; return; }

        // 选择存档文件（zip）
        var window = (Window?)TopLevel.GetTopLevel(this);
        if (window == null) { saveHint.Text = "无法打开文件选择器"; return; }

        var storage = window.StorageProvider;
        if (storage is null) { saveHint.Text = "存储提供者不可用"; return; }
        var zipType = new Avalonia.Platform.Storage.FilePickerFileType("存档文件 (*.zip)") { Patterns = new[] { "*.zip" } };
        var allType = new Avalonia.Platform.Storage.FilePickerFileType("所有文件") { Patterns = new[] { "*" } };
        var files = await storage.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = "选择存档文件（.zip）",
            AllowMultiple = false,
            FileTypeFilter = new[] { zipType, allType },
        });

        if (files.Count == 0) return;

        var file = files[0].Path.LocalPath;
        saveHint.Text = "正在上传…（计算 SHA-256）";

        var label = Path.GetFileNameWithoutExtension(file);
        var progress = new Progress<string>(msg => saveHint.Text = msg);
        var result = await _uploader.UploadAndPublishAsync(
            filePath: file,
            label: label,
            game: "ets2",
            gameVersion: "1.61",
            releaseNote: null,
            stage: progress);

        if (result.Ok)
            saveHint.Text = $"✅ 上传成功{(!string.IsNullOrEmpty(result.Error) ? "（" + result.Error + "）" : "并已发布")}";
        else
            saveHint.Text = "❌ 上传失败：" + result.Error;
    }

    // ---------- 其他 ----------

    private void OnSaveServerClick(object? sender, RoutedEventArgs e)
    {
        serverStatus.Text = "服务端凭据（S3 secret / GitHub token）保存在后端 .env，不在客户端（§2.4 安全规则）。请在服务器侧配置。";
    }

    private void OnUploadImageClick(object? sender, RoutedEventArgs e)
        => imgHint.Text = "活动图上传：复用存档直传通道（upload-credential → PUT），P2 实装";

    // ---------- 工具方法 ----------

    private async Task<string?> SendJsonAsync(HttpMethod method, string path, object body)
    {
        using var req = new System.Net.Http.HttpRequestMessage(method,
            new Uri(_api!.ApiBase + "/" + path.TrimStart('/')));
        req.Content = System.Net.Http.Json.JsonContent.Create(body,
            options: ApiClient.JsonOpts);
        // 注入 Bearer
        var token = (AppServiceLocator.Services?.GetService<IAuthService>())?.AccessToken;
        if (!string.IsNullOrWhiteSpace(token))
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        using var resp = await _api.Http.SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"HTTP {(int)resp.StatusCode}：{text[..Math.Min(text.Length, 200)]}");
        return text;
    }

    private static Guid? ExtractGuid(string json, string field)
    {
        try
        {
            var obj = System.Text.Json.JsonDocument.Parse(json);
            if (obj.RootElement.TryGetProperty(field, out var v))
            {
                if (Guid.TryParse(v.GetString(), out var g)) return g;
                if (long.TryParse(v.GetString(), out var l)) return new Guid(l.ToString("x"));
            }
        }
        catch { }
        return null;
    }
}

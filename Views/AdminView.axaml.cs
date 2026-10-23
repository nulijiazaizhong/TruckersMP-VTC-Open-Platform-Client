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

    // ---------- 用户管理（成员 CRUD：C13 P1） ----------

    private async void OnAddUserClick(object? sender, RoutedEventArgs e)
    {
        if (_api == null) { SetUsrStatus("API 不可用", false); return; }
        var username = usrUsername.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(username)) { SetUsrStatus("请输入用户名", false); return; }
        var role = ParseRole(usrRole.SelectedIndex);

        // 密码：留空 = 客户端生成强随机密码（服务端 Rust 要求非空且不自带"空则随机"逻辑，
        // 响应也不回传密码，故由客户端生成并在成功后一次性显示，告知用户改密）。
        var typed = usrPassword.Text ?? "";
        var randomMode = string.IsNullOrEmpty(typed);
        var password = randomMode ? GenerateRandomPassword(14) : typed;

        btnAddUser.IsEnabled = false;
        btnAddUser.Content = "添加中…";
        try
        {
            var body = new { username, role, password };
            // 用 ApiClient（401 自动 refresh 后重试），避免裸请求 token 过期 401
            var json = await _api.PostAsync<string>(VtcApi.Members, body, useAuth: true);
            if (!string.IsNullOrEmpty(json))
            {
                // 关键：随机模式下把生成的初始密码显式展示给用户（仅此一次）
                SetUsrStatus(randomMode
                    ? $"✅ 已添加 {username}（{role}），初始密码：{password} —— 请告知用户登录后立即修改"
                    : $"✅ 已添加 {username}（{role}）", true);
                usrUsername.Text = "";
                usrPassword.Text = "";
                await RefreshUsersAsync();
            }
        }
        catch (Exception ex)
        {
            SetUsrStatus("添加失败：" + ex.Message, false);
        }
        finally
        {
            btnAddUser.IsEnabled = true;
            btnAddUser.Content = "添加用户";
        }
    }

    private async void OnRefreshUsersClick(object? sender, RoutedEventArgs e) => await RefreshUsersAsync();

    private async Task RefreshUsersAsync()
    {
        if (_api == null) return;
        try
        {
            // 用 ApiClient.GetStringAsync(useAuth:true)：401 自动 refresh（裸请求会 token 过期 401）
            var json = await _api.GetStringAsync(VtcApi.Members, new Dictionary<string, string?> { ["take"] = "200" }, useAuth: true);
            var doc = System.Text.Json.JsonDocument.Parse(json);
            var listEl = doc.RootElement.TryGetProperty("items", out var it) ? it
                : (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array ? doc.RootElement : default(System.Text.Json.JsonElement));
            var count = doc.RootElement.TryGetProperty("total", out var tt) && tt.ValueKind == System.Text.Json.JsonValueKind.Number ? tt.GetInt32() : -1;

            membersList.Children.Clear();
            int n = 0;
            if (listEl.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var m in listEl.EnumerateArray())
                {
                    string mid = TryStr(m, "id");
                    string username = TryStr(m, "username");
                    string role = TryStr(m, "role");
                    string status = TryStr(m, "status");
                    string plate = TryStr(m, "plate");
                    string vehicle = TryStr(m, "vehicle");

                    var row = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Margin = new Avalonia.Thickness(0, 0, 0, 6), Spacing = 10 };
                    row.Children.Add(new TextBlock { Text = username, FontSize = 14, FontWeight = Avalonia.Media.FontWeight.SemiBold, MinWidth = 140 });
                    row.Children.Add(new TextBlock { Text = "  " + (string.IsNullOrEmpty(role) ? "-" : role), FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(0xC0, 0xA8, 0x4E)), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
                    var info = (plate + " " + vehicle + (string.IsNullOrEmpty(status) ? "" : " · " + status)).Trim();
                    if (!string.IsNullOrEmpty(info))
                        row.Children.Add(new TextBlock { Text = info, FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x90, 0x99)), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
                    if (!string.IsNullOrEmpty(mid))
                    {
                        var del = new Button { Content = "删除", Width = 56, Height = 26, Margin = new Avalonia.Thickness(8, 0, 0, 0) };
                        del.Tag = (mid, username);
                        del.Click += OnDeleteUserClick;
                        row.Children.Add(del);
                    }
                    membersList.Children.Add(row);
                    n++;
                }
            }
            usrCount.Text = count >= 0 ? $"共 {count} 名账号（显示前 {n}）" : $"显示 {n} 名账号";
            if (n == 0) membersList.Children.Add(new TextBlock { Text = "（暂无账号）", FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x90, 0x99)) });
        }
        catch (Exception ex)
        {
            usrCount.Text = "加载账号列表失败：" + ex.Message;
        }
    }

    private async void OnDeleteUserClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not (string id, string name)) return;
        var confirm = new Window
        {
            Width = 400, Height = 180, Title = "确认删除", WindowStartupLocation = WindowStartupLocation.CenterScreen,
        };
        var panel = new StackPanel { Margin = new Avalonia.Thickness(22), Spacing = 14 };
        panel.Children.Add(new TextBlock { Text = $"确认删除账号「{name}」？（软删除：标记退队，不物理删除）", TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        var btns = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 12 };
        var ok = new Button { Content = "删除", Width = 88, Height = 32, Background = new SolidColorBrush(Color.FromRgb(0xF4, 0x43, 0x36)), Foreground = Brushes.White };
        var cancel = new Button { Content = "取消", Width = 88, Height = 32 };
        cancel.Click += (_, _) => confirm.Close();
        ok.Click += async (_, _) =>
        {
            confirm.Close();
            try
            {
                await _api.DeleteAsync<string>(VtcApi.MemberDetail.Replace("{id}", id), useAuth: true);
                await RefreshUsersAsync();
            }
            catch (Exception ex) { SetUsrStatus("删除失败：" + ex.Message, false); }
        };
        btns.Children.Add(ok); btns.Children.Add(cancel);
        panel.Children.Add(btns);
        confirm.Content = panel;
        confirm.Show();
    }

    private static string ParseRole(int index) => index switch
    {
        0 => "Admin",
        1 => "Manager",
        2 => "Driver",
        _ => "Member",
    };

    /// <summary>生成强随机初始密码（大小写字母+数字+至少一个符号，长度默认 14）。
    /// 服务端 Rust 不生成随机密码，故由客户端生成后明文发给服务端（本地/自部署可接受），
    /// 并一次性显示给管理员转交用户。</summary>
    private static string GenerateRandomPassword(int length = 14)
    {
        const string alpha = "abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        const string special = "!@#$%^&*";
        var buf = System.Security.Cryptography.RandomNumberGenerator.GetBytes(length);
        var sb = new System.Text.StringBuilder(length);
        foreach (var b in buf) sb.Append(alpha[b % alpha.Length]);
        // 保证至少一个符号（随机位置）
        sb[buf[length / 2] % special.Length] = special[buf[length / 2] % special.Length];
        return sb.ToString();
    }

    private static string TryStr(System.Text.Json.JsonElement e, string field)
    {
        if (e.TryGetProperty(field, out var v))
        {
            if (v.ValueKind == System.Text.Json.JsonValueKind.String) return v.GetString() ?? "";
            return v.ToString();
        }
        return "";
    }

    private void SetUsrStatus(string text, bool ok)
    {
        usrStatus.Text = text;
        usrStatus.Foreground = new SolidColorBrush(ok ? Color.FromRgb(0x1B, 0x7A, 0x43) : Color.FromRgb(0xF4, 0x43, 0x36));
    }

    private async Task<string?> GetJsonAsync(string path)
    {
        using var req = new System.Net.Http.HttpRequestMessage(HttpMethod.Get,
            new Uri(_api!.ApiBase + "/" + path.TrimStart('/')));
        var token = (AppServiceLocator.Services?.GetService<IAuthService>())?.AccessToken;
        if (!string.IsNullOrWhiteSpace(token))
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        using var resp = await _api.Http.SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"HTTP {(int)resp.StatusCode}：{text[..Math.Min(text.Length, 200)]}");
        return text;
    }

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

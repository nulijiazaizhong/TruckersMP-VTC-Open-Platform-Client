using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace VTC.Client.Services;

/// <summary>
/// WebSocket 推送客户端（P0 预留接口，不强制完整实现推送）。
/// 规划 §4.1：WebSockets（ClientWebSocket 封装），用于公告/活动/VTC 事件实时下发。
/// P0：提供 Connect/ReceiveLoop/Close 骨架 + 心跳，收到消息后触发事件（由 VM 订阅）。
/// </summary>
public sealed class PushClient : IAsyncDisposable
{
    private readonly IConfiguration _cfg;
    private readonly ILogger<PushClient> _log;
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private Task? _receiveLoop;
    private bool _disposed;

    public bool IsConnected => _ws?.State == WebSocketState.Open;

    /// <summary>收到一条推送（原始 JSON 文本）。订阅方按需解析。</summary>
    public event Action<string>? MessageReceived;

    public PushClient(IConfiguration cfg, ILogger<PushClient> log)
    {
        _cfg = cfg;
        _log = log;
    }

    public async Task<bool> ConnectAsync()
    {
        var wsBase = EnvLoader.Get(_cfg, "VTC_WS_BASE");
        if (string.IsNullOrWhiteSpace(wsBase))
        {
            // 由 API 推导：http→ws, https→wss
            var api = EnvLoader.Get(_cfg, "VTC_API_BASE");
            if (api == null) return false;
            wsBase = api.Replace("http://", "ws://").Replace("https://", "wss://");
        }
        if (!wsBase.EndsWith("/")) wsBase += "/";
        var url = wsBase + "ws";

        try
        {
            _ws = new ClientWebSocket();
            _cts = new CancellationTokenSource();
            await _ws.ConnectAsync(new Uri(url), _cts.Token);
            _receiveLoop = Task.Run(() => ReceiveLoopAsync());
            _log.LogInformation("WebSocket 已连接：{Url}", url);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning("WebSocket 连接失败（推送不可用，功能降级为轮询）：{Ex}", ex.Message);
            return false;
        }
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[8192];
        var sb = new StringBuilder();
        try
        {
            while (_ws != null && _ws.State == WebSocketState.Open && !_cts!.IsCancellationRequested)
            {
                sb.Clear();
                WebSocketReceiveResult result;
                do
                {
                    result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        _log.LogInformation("WebSocket 服务端关闭");
                        return;
                    }
                    sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                } while (!result.EndOfMessage);

                var msg = sb.ToString();
                try { MessageReceived?.Invoke(msg); }
                catch (Exception ex) { _log.LogWarning("推送处理异常：{Ex}", ex.Message); }
            }
        }
        catch (OperationCanceledException) { /* 正常关闭 */ }
        catch (Exception ex)
        {
            _log.LogWarning("WebSocket 接收循环异常：{Ex}", ex.Message);
        }
    }

    public async Task CloseAsync()
    {
        try
        {
            _cts?.Cancel();
            if (_ws != null && _ws.State == WebSocketState.Open)
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        }
        catch { /* ignore */ }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await CloseAsync();
        _ws?.Dispose();
        _cts?.Dispose();
        if (_receiveLoop != null)
        {
            try { await _receiveLoop; } catch { /* ignore */ }
        }
    }
}

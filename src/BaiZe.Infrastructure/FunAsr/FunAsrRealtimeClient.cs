using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using BaiZe.Domain.Ports;
using Microsoft.Extensions.Logging;

namespace BaiZe.Infrastructure.FunAsr;

/// <summary>
/// FunASR 运行时 WebSocket 流式客户端（真实实现）。
/// 协议：连接后先发一条 JSON 会话配置（2pass 模式、16k pcm、is_speaking=true），
/// 随后持续发二进制 PCM 帧；服务端回 JSON（mode=online 为局部结果，offline / is_final=true 为定稿）；
/// 结束时发 {"is_speaking": false} 冲刷尾句。
/// 端口在 ConnectAsync 时从 FunAsrOptions 动态读取（FunAsrServerHost 换端口后自动跟随）；
/// 服务端可能仍在加载模型，连接失败自动重试。
/// </summary>
public sealed class FunAsrRealtimeClient : IRealtimeTranscriber
{
    private readonly FunAsrOptions _options;
    private readonly ILogger<FunAsrRealtimeClient> _logger;
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _recvCts;

    public bool IsConnected => _ws?.State == WebSocketState.Open;

    public event Action<AsrEvent>? ResultReceived;
    public event Action<string>? StatusChanged;

    public FunAsrRealtimeClient(FunAsrOptions options, ILogger<FunAsrRealtimeClient> logger)
    {
        _options = options;
        _logger = logger;
    }

    public async Task ConnectAsync(string sessionName, CancellationToken ct = default)
    {
        // 冷启动可能要 90 秒以上（首次加载 5 个模型 + 分离预热），不能设次数上限——
        // 设了上限就会在服务端就绪前耗尽，异常又抛进 fire-and-forget 的 task 里，
        // UI 永远停在"正在连接(10/10)"。改为一直重试，直到 ct 取消（结束会议）或连上。
        const int retryDelayMs = 2000;

        for (var attempt = 1; ; attempt++)
        {
            StatusChanged?.Invoke($"正在连接语音识别服务（{_options.WsUrl}）…（第 {attempt} 次）");
            try
            {
                await ConnectOnceAsync(sessionName, ct).ConfigureAwait(false);
                StatusChanged?.Invoke("已连接 · 实时识别中");
                return;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "FunASR 连接失败（第 {Attempt} 次），{Delay}ms 后重试", attempt, retryDelayMs);
                StatusChanged?.Invoke($"等待语音识别服务就绪…（第 {attempt} 次）");
                await Task.Delay(retryDelayMs, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task ConnectOnceAsync(string sessionName, CancellationToken ct)
    {
        _ws?.Dispose();
        _ws = new ClientWebSocket();
        await _ws.ConnectAsync(new Uri(_options.WsUrl), ct).ConfigureAwait(false);

        var config = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["mode"] = "2pass",
            ["chunk_size"] = new[] { 5, 10, 5 },
            ["chunk_interval"] = 10,
            ["wav_name"] = sessionName,
            ["wav_format"] = "pcm",
            ["audio_fs"] = 16000,
            ["itn"] = true,
            ["is_speaking"] = true,
            ["spk_model"] = string.IsNullOrWhiteSpace(_options.SpkModel) ? null : _options.SpkModel
        });
        await SendTextAsync(config, ct).ConfigureAwait(false);

        _recvCts = new CancellationTokenSource();
        _ = Task.Run(() => ReceiveLoopAsync(_recvCts.Token));
        _logger.LogInformation("FunASR WS 已连接: {Url}", _options.WsUrl);
    }

    public async Task SendAudioAsync(byte[] pcmChunk, CancellationToken ct = default)
    {
        if (!IsConnected || pcmChunk.Length == 0) return;
        await _ws!.SendAsync(pcmChunk, WebSocketMessageType.Binary, true, ct).ConfigureAwait(false);
    }

    public async Task FinishAsync(CancellationToken ct = default)
    {
        if (!IsConnected) return;
        try
        {
            await SendTextAsync("{\"is_speaking\": false}", ct).ConfigureAwait(false);
            await Task.Delay(300, CancellationToken.None).ConfigureAwait(false);   // 留时间收尾句
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "发送 is_speaking=false 失败");
        }
        finally
        {
            await CloseAsync().ConfigureAwait(false);
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (!ct.IsCancellationRequested && _ws is { State: WebSocketState.Open })
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await _ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        StatusChanged?.Invoke("FunASR 连接已关闭");
                        await CloseAsync().ConfigureAwait(false);
                        return;
                    }
                    ms.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                var text = Encoding.UTF8.GetString(ms.ToArray());
                HandleServerMessage(text);
            }
        }
        catch (OperationCanceledException) { /* 正常关闭 */ }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FunASR 接收循环异常");
            StatusChanged?.Invoke($"FunASR 连接中断：{ex.Message}");
        }
    }

    private void HandleServerMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var text = root.TryGetProperty("text", out var t) ? t.GetString() : null;
            if (string.IsNullOrWhiteSpace(text)) return;

            var mode = root.TryGetProperty("mode", out var m) ? m.GetString() : null;
            var isFinal = root.TryGetProperty("is_final", out var f) && f.ValueKind == JsonValueKind.True
                          || mode == "offline";   // offline / is_final=true 均视为定稿
            // 说话人聚类编号（cam++）；服务端未启用/未回传时为 -1
            var spk = root.TryGetProperty("spk", out var s) && s.ValueKind == JsonValueKind.Number
                ? s.GetInt32() : -1;
            ResultReceived?.Invoke(new AsrEvent(text, isFinal, spk));
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "FunASR 消息解析失败: {Json}", json);
        }
    }

    private async Task SendTextAsync(string text, CancellationToken ct) =>
        await _ws!.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, ct)
            .ConfigureAwait(false);

    private async Task CloseAsync()
    {
        _recvCts?.Cancel();
        if (_ws is { State: WebSocketState.Open })
        {
            try { await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None).ConfigureAwait(false); }
            catch { /* 已断开时忽略 */ }
        }
        _ws?.Dispose();
        _ws = null;
    }

    public async ValueTask DisposeAsync() => await CloseAsync().ConfigureAwait(false);
}

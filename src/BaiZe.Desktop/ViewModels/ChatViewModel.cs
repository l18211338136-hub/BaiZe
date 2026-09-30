using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Threading;
using BaiZe.Application;
using BaiZe.Domain.Ports;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BaiZe.Desktop.ViewModels;

/// <summary>消息基类：区分用户 / 助手 / 工具执行卡片三种气泡，各自有独立 DataTemplate。</summary>
public abstract record ChatMessage;

public sealed record UserMsg(string Text) : ChatMessage;

public sealed record AssistantMsg(string Text) : ChatMessage;

/// <summary>工具执行卡片：标题徽标 + 参数（mono）+ 说明，对应 CommandResultDto。</summary>
public sealed record ToolMsg : ChatMessage
{
    public string Title { get; init; } = "工具执行";
    public string? Params { get; init; }
    public string? Detail { get; init; }
    public bool Success { get; init; }

    public bool HasParams => !string.IsNullOrEmpty(Params);
    public string Badge => Success ? "执行成功" : "执行失败";

    private static readonly IBrush OkBg = SolidColorBrush.Parse("#E1F5EE");
    private static readonly IBrush OkFg = SolidColorBrush.Parse("#0F6E56");
    private static readonly IBrush BadBg = SolidColorBrush.Parse("#FCEBEB");
    private static readonly IBrush BadFg = SolidColorBrush.Parse("#A32D2D");

    public IBrush BadgeBackground => Success ? OkBg : BadBg;
    public IBrush BadgeForeground => Success ? OkFg : BadFg;
}

/// <summary>
/// 对话页：消息流 + 文本/语音双通道输入。
/// 语音输入复用会议页同一套链路：麦克风 → PortAudioCapture → IRealtimeTranscriber（FunASR 流式）。
/// ⚠️ IRealtimeTranscriber / IAudioCapture 是单例（同一时刻只能一路 WebSocket + 一个麦克风），
///    所以对话页语音与会议录制互斥：任一方占用时另一方拒绝启动。
/// </summary>
public partial class ChatViewModel : ObservableObject
{
    private readonly IBaiZeFacade _facade;
    private readonly IRealtimeTranscriber _transcriber;
    private readonly IAudioCapture _capture;
    private CancellationTokenSource? _voiceCts;

    [ObservableProperty] private string _inputText = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isListening;
    [ObservableProperty] private string _voiceStatus = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VoiceHint))]
    private string _partialText = "";

    public ObservableCollection<ChatMessage> Messages { get; } = new();

    /// <summary>聆听中提示：有流式中间结果就显示它，否则显示"聆听中…"。</summary>
    public string VoiceHint => string.IsNullOrWhiteSpace(PartialText) ? "聆听中…请说话" : PartialText;

    public ChatViewModel(IBaiZeFacade facade, IRealtimeTranscriber transcriber, IAudioCapture capture)
    {
        _facade = facade;
        _transcriber = transcriber;
        _capture = capture;
        // 单例客户端的结果事件对两个页面都会广播，只在自己开启语音时才处理（否则会把会议转写灌进输入框）
        _transcriber.ResultReceived += e => Dispatcher.UIThread.Post(() => ApplyVoice(e));
        _transcriber.StatusChanged += s => Dispatcher.UIThread.Post(() =>
        {
            if (!IsListening) return;
            VoiceStatus = s.Contains("已连接") ? "语音识别已就绪" : s;
        });

        Messages.Add(new AssistantMsg(
            "你好，我是白泽。可以直接打字，也可以点麦克风用语音输入。我能帮你打开应用、查报表、订机票，也能为你记录会议。"));
    }

    // ============ 语音输入：麦克风 → FunASR 流式识别 → 填入输入框 ============

    [RelayCommand]
    private async Task ToggleVoiceAsync()
    {
        if (IsListening) await StopVoiceAsync();
        else await StartVoiceAsync();
    }

    private async Task StartVoiceAsync()
    {
        // 单例客户端已被占用（会议正在录制）→ 拒绝启动，避免两路抢同一个 WebSocket / 麦克风
        if (_transcriber.IsConnected)
        {
            VoiceStatus = "语音服务正忙（会议录制中）";
            return;
        }
        try
        {
            // 注意：这里不用 ConfigureAwait(false)——await 之后还要改绑定属性，必须留在 UI 线程
            await _capture.StartAsync();
            _voiceCts = new CancellationTokenSource();
            _ = Task.Run(() => PumpAsync(_voiceCts.Token));
            _ = _transcriber.ConnectAsync("chat-" + Guid.NewGuid().ToString("N")[..8], _voiceCts.Token);
            IsListening = true;
            PartialText = "";
            VoiceStatus = "正在连接语音识别服务…";
        }
        catch (Exception ex)
        {
            await StopVoiceAsync();
            VoiceStatus = $"麦克风不可用：{ex.Message}";
        }
    }

    private async Task StopVoiceAsync()
    {
        _voiceCts?.Cancel();
        _voiceCts?.Dispose();
        _voiceCts = null;
        try { await _transcriber.FinishAsync(); } catch { /* 服务端不在时忽略 */ }
        try { await _capture.StopAsync(); } catch { /* 设备异常不阻断 */ }
        IsListening = false;
        PartialText = "";
        VoiceStatus = "";
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var chunk in _capture.CaptureAsync(ct))
            {
                if (_transcriber.IsConnected)
                    await _transcriber.SendAudioAsync(chunk, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* 正常停止 */ }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() => VoiceStatus = $"音频中断：{ex.Message}");
        }
    }

    /// <summary>流式结果：partial 只做实时提示，定稿才拼进输入框（避免输入内容被反复改写）。</summary>
    private void ApplyVoice(AsrEvent e)
    {
        if (!IsListening) return;

        if (!e.IsFinal)
        {
            PartialText = e.Text;
            return;
        }

        PartialText = "";
        var t = e.Text?.Trim();
        if (string.IsNullOrEmpty(t)) return;
        InputText = string.IsNullOrWhiteSpace(InputText) ? t : InputText.TrimEnd() + t;
    }

    [RelayCommand]
    private void UseQuick(string text) => InputText = text;

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        var text = InputText?.Trim();
        if (string.IsNullOrEmpty(text) || IsBusy) return;

        // 语音还在听就先停：否则麦克风和 WebSocket 一直占着，且后续识别结果会污染输入框。
        // ⚠️ 这里不能用 ConfigureAwait(false)——StopVoiceAsync 之后还要改绑定属性、Add 消息，
        //    必须留在 UI 线程；否则后续代码会落到线程池线程，触发 ScrollToEnd 的线程校验崩溃。
        if (IsListening) await StopVoiceAsync();

        Messages.Add(new UserMsg(text));
        InputText = "";
        IsBusy = true;
        try
        {
            var r = await _facade.ExecuteVoiceCommandAsync(text);
            if (!string.IsNullOrEmpty(r.DataJson))
                Messages.Add(new ToolMsg { Params = r.DataJson, Detail = r.Message, Success = r.Success });
            Messages.Add(new AssistantMsg(r.Message ?? (r.Success ? "已完成。" : "抱歉，我没听懂这条指令。")));
        }
        catch (Exception ex)
        {
            Messages.Add(new AssistantMsg("出错了：" + ex.Message));
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>发送按钮可用性：有非空输入且不在执行中（驱动按钮置灰）。</summary>
    private bool CanSend() => !string.IsNullOrWhiteSpace(InputText) && !IsBusy;

    partial void OnInputTextChanged(string value) => SendCommand.NotifyCanExecuteChanged();
    partial void OnIsBusyChanged(bool value) => SendCommand.NotifyCanExecuteChanged();
}

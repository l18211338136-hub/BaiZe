using System.Collections.ObjectModel;
using Avalonia.Threading;
using BaiZe.Application;
using BaiZe.Contracts;
using BaiZe.Domain.Ports;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BaiZe.Desktop.ViewModels;

public sealed record SegmentVm(string Name, string Time, string Text, bool IsPartial = false)
{
    public string Initial => Name.StartsWith("说话人") ? Name[^1].ToString() : Name[..1];
    /// <summary>partial 结果灰色显示，final 定稿深色。</summary>
    public string TextColor => IsPartial ? "#9A988E" : "#33332E";
}

public sealed record SpeakerVm(string Initial, string Name);

/// <summary>
/// 会议页：录制状态头 + 实时转写流（麦克风 → FunASR WebSocket 流式识别）+ 参会人/统计 + 纪要。
/// 麦克风音频经 PortAudioCapture 采集（16k/mono/16bit PCM），由 IRealtimeTranscriber 推流，
/// partial 结果原位覆盖、final 定稿；识别服务不可用时状态栏提示，不影响会议计时。
/// </summary>
public partial class MeetingViewModel : ObservableObject
{
    private readonly IBaiZeFacade _facade;
    private readonly IRealtimeTranscriber _transcriber;
    private readonly IAudioCapture _capture;
    private DispatcherTimer? _timer;
    private DateTime _startedAt;
    private Guid _meetingId;
    private CancellationTokenSource? _pumpCts;
    private int _partialIndex = -1;

    [ObservableProperty] private bool _hasMeeting;
    [ObservableProperty] private bool _isRecording;
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _meetingIdText = "";
    [ObservableProperty] private string _elapsedText = "00:00:00";
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private bool _hasSummary;
    [ObservableProperty] private int _wordCount;
    [ObservableProperty] private int _segmentCount;
    [ObservableProperty] private bool _isBusySummary;
    [ObservableProperty] private string _micStatus = "麦克风未开启 · 点击「开始会议」";
    [ObservableProperty] private bool _isMicLive;
    [ObservableProperty] private bool _isAsrConnected;

    public ObservableCollection<SegmentVm> Segments { get; } = new();
    public ObservableCollection<SpeakerVm> Speakers { get; } = new();
    public bool HasSpeakers => Speakers.Count > 0;

    public MeetingViewModel(IBaiZeFacade facade, IRealtimeTranscriber transcriber, IAudioCapture capture)
    {
        _facade = facade;
        _transcriber = transcriber;
        _capture = capture;
        _transcriber.ResultReceived += e => Dispatcher.UIThread.Post(() => ApplyAsr(e));
        _transcriber.StatusChanged += s => Dispatcher.UIThread.Post(() =>
        {
            // 连接成功：点亮"已连接"徽标，状态文案精简为"实时识别中"（避免与徽标重复）
            if (s.Contains("已连接"))
            {
                IsAsrConnected = true;
                MicStatus = "实时识别中";
            }
            else
            {
                IsAsrConnected = false;
                MicStatus = s;
            }
        });
    }

    [RelayCommand]
    private async Task StartMeetingAsync()
    {
        if (IsRecording) return;
        var m = await _facade.StartMeetingAsync("会议 " + DateTime.Now.ToString("HH:mm"));
        _meetingId = m.Id;
        Title = m.Title;
        MeetingIdText = m.Id.ToString("N")[..8];
        HasMeeting = true;
        IsRecording = true;
        HasSummary = false;
        Summary = "";
        LoadMeeting(m);

        _startedAt = DateTime.Now;
        ElapsedText = "00:00:00";
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) =>
        {
            var t = DateTime.Now - _startedAt;
            ElapsedText = $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";
        });
        _timer.Start();

        StartTranscription();
    }

    [RelayCommand]
    private async Task EndMeetingAsync()
    {
        IsRecording = false;
        _timer?.Stop();
        await StopTranscriptionAsync();
        MicStatus = "麦克风已关闭 · 会议结束";
        IsMicLive = false;
        IsAsrConnected = false;
    }

    // ============ 实时转写：麦克风泵 + FunASR WebSocket ============

    private void StartTranscription()
    {
        try
        {
            _capture.StartAsync().ConfigureAwait(false).GetAwaiter().GetResult();
            _pumpCts = new CancellationTokenSource();
            _ = Task.Run(() => PumpAsync(_pumpCts.Token));
            _ = _transcriber.ConnectAsync("meeting-" + _meetingId.ToString("N")[..8], _pumpCts.Token);
            IsMicLive = true; // 采集已启动：点亮"麦克风在线"指示灯（失败路径在 catch 熄灭）
        }
        catch (Exception ex)
        {
            MicStatus = $"麦克风不可用：{ex.Message}";
            IsMicLive = false;
        }
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var chunk in _capture.CaptureAsync(ct))
            {
                if (_transcriber.IsConnected)
                    await _transcriber.SendAudioAsync(chunk, ct);
            }
        }
        catch (OperationCanceledException) { /* 停止会议时正常取消 */ }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() => MicStatus = $"音频泵中断：{ex.Message}");
        }
    }

    private async Task StopTranscriptionAsync()
    {
        _pumpCts?.Cancel();
        _pumpCts?.Dispose();
        _pumpCts = null;
        try { await _transcriber.FinishAsync(); } catch { /* 服务端不在时忽略 */ }
        try { await _capture.StopAsync(); } catch { /* 设备异常不阻断结束流程 */ }
        _partialIndex = -1;
    }

    /// <summary>ASR 结果合并：partial 原位覆盖最后一段，final 定稿并让下一条 partial 开新段。</summary>
    private void ApplyAsr(AsrEvent e)
    {
        if (!IsRecording) return;
        var t = DateTime.Now - _startedAt;
        var time = $"{(int)t.TotalMinutes:00}:{t.Seconds:00}";
        var name = EnsureSpeaker(e.SpeakerId);

        if (!e.IsFinal && _partialIndex >= 0 && _partialIndex < Segments.Count)
        {
            var old = Segments[_partialIndex];
            Segments[_partialIndex] = old with { Text = e.Text };
            return;
        }

        if (e.IsFinal && _partialIndex >= 0 && _partialIndex < Segments.Count)
        {
            var old = Segments[_partialIndex];
            // 流式 partial 不带说话人（-1）；定稿带上真实聚类编号后，据此更新该段的发言人
            var finalName = e.SpeakerId >= 0 ? EnsureSpeaker(e.SpeakerId) : old.Name;
            Segments[_partialIndex] = new SegmentVm(finalName, old.Time, e.Text, IsPartial: false);
            _partialIndex = -1;
        }
        else
        {
            var vm = new SegmentVm(name, time, e.Text, IsPartial: !e.IsFinal);
            Segments.Add(vm);
            if (!e.IsFinal) _partialIndex = Segments.Count - 1;
        }

        SegmentCount = Segments.Count;
        WordCount = Segments.Sum(x => x.Text.Length);
    }

    /// <summary>按说话人聚类编号取显示名（"说话人 1/2..."），首次出现时登记到参会人面板；
    /// spk = -1（未启用分离）回落为"我（本机）"。</summary>
    private string EnsureSpeaker(int spk)
    {
        if (spk < 0) return "我（本机）";
        var name = $"说话人 {spk + 1}";
        if (Speakers.All(x => x.Name != name))
        {
            Speakers.Add(new SpeakerVm(name[^1].ToString(), name));
            OnPropertyChanged(nameof(HasSpeakers));
        }
        return name;
    }

    [RelayCommand]
    private async Task SummarizeAsync()
    {
        if (!HasMeeting || IsBusySummary) return;
        IsBusySummary = true;
        try
        {
            var m = await _facade.SummarizeMeetingAsync(_meetingId);
            Summary = string.IsNullOrWhiteSpace(m.Summary)
                ? "（纪要为空：LLM 网关尚未接入，Summarize 目前返回空文本。）"
                : m.Summary;
            HasSummary = true;
            LoadMeeting(m);
        }
        finally
        {
            IsBusySummary = false;
        }
    }

    private void LoadMeeting(MeetingDto m)
    {
        Speakers.Clear();
        foreach (var sp in m.Speakers)
        {
            var name = sp.DisplayName ?? "说话人 " + sp.SpeakerId[..Math.Min(4, sp.SpeakerId.Length)];
            Speakers.Add(new SpeakerVm(name[..1], name));
        }
        OnPropertyChanged(nameof(HasSpeakers));

        Segments.Clear();
        _partialIndex = -1;
        foreach (var s in m.Segments)
        {
            var ts = TimeSpan.FromMilliseconds(s.StartMs);
            Segments.Add(new SegmentVm(ResolveSpeakerName(s.SpeakerId),
                $"{(int)ts.TotalMinutes:00}:{ts.Seconds:00}", s.Text));
        }
        SegmentCount = Segments.Count;
        WordCount = m.Segments.Sum(x => x.Text.Length);
    }

    private string ResolveSpeakerName(string speakerId)
    {
        var match = Speakers.FirstOrDefault(x => x.Name.Contains(speakerId) || speakerId.Contains(x.Name));
        return match?.Name ?? "说话人 " + speakerId[..Math.Min(4, speakerId.Length)];
    }
}

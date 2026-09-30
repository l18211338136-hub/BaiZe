namespace BaiZe.Domain.Ports;

/// <summary>音频采集端口：跨平台麦克风采集（真实实现用 PortAudioSharp2，首版为桩）。</summary>
public interface IAudioCapture : IAsyncDisposable
{
    Task StartAsync(CancellationToken ct = default);

    /// <summary>持续产出 16k 单声道 PCM 音频块。</summary>
    IAsyncEnumerable<byte[]> CaptureAsync(CancellationToken ct = default);

    Task StopAsync();
}

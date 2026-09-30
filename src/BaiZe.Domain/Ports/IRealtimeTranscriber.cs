namespace BaiZe.Domain.Ports;

/// <summary>实时 ASR 事件：partial 结果可被后续事件覆盖，final 为定稿文本。
/// SpeakerId 为服务端说话人分离（cam++）输出的聚类编号，0 起始；-1 = 未知/未启用分离。</summary>
public sealed record AsrEvent(string Text, bool IsFinal, int SpeakerId = -1);

/// <summary>
/// 实时转写端口（六边形适配器接口）：连接 ASR 服务、持续推送 PCM 音频块、按事件回传识别结果。
/// 与 IAsrGateway.StreamAsync（pull 流）不同，本端口是 push 模型，直接映射 FunASR WebSocket 协议。
/// </summary>
public interface IRealtimeTranscriber : IAsyncDisposable
{
    /// <summary>WebSocket 是否处于已连接状态。</summary>
    bool IsConnected { get; }

    /// <summary>识别结果（partial / final）。回调在后台线程触发，订阅方自行调度到 UI 线程。</summary>
    event Action<AsrEvent>? ResultReceived;

    /// <summary>人类可读的连接状态变化（连接中 / 已连接 / 失败原因等）。</summary>
    event Action<string>? StatusChanged;

    /// <summary>建立连接并发送会话配置（采样率、模式等）。失败抛异常，由调用方决定降级。</summary>
    Task ConnectAsync(string sessionName, CancellationToken ct = default);

    /// <summary>推送一块 16k / 单声道 / 16bit PCM 音频。</summary>
    Task SendAudioAsync(byte[] pcmChunk, CancellationToken ct = default);

    /// <summary>通知服务端语音结束（is_speaking=false），冲刷最终识别结果。</summary>
    Task FinishAsync(CancellationToken ct = default);
}

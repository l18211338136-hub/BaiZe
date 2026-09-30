using BaiZe.Domain.Meetings;

namespace BaiZe.Domain.Ports;

/// <summary>ASR 原始段：含局部说话人编号 + 声纹向量 + 文本 + 时间。声纹向量用于后端声纹归并。</summary>
public sealed record RawSegment(SpeakerId SpeakerId, float[] Voiceprint, string Text, long StartMs, long EndMs);

public sealed record AsrResult(IReadOnlyList<RawSegment> Segments);

/// <summary>ASR 网关端口（六边形适配器接口）：本地 FunASR(cuda) 实现，或未来替换其他引擎。</summary>
public interface IAsrGateway
{
    /// <summary>整段转写（非流式）。</summary>
    Task<AsrResult> TranscribeAsync(Stream audio, CancellationToken ct = default);

    /// <summary>流式转写（VAD+ASR+标点+说话人逐段回传），用于实时字幕。</summary>
    IAsyncEnumerable<RawSegment> StreamAsync(Stream audio, CancellationToken ct = default);
}

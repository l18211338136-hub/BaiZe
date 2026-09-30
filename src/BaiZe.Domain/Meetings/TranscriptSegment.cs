namespace BaiZe.Domain.Meetings;

/// <summary>转写段落（值对象）：某说话人在某时间段说了什么。声纹向量不进段落，由 Speaker 持有。</summary>
public sealed record TranscriptSegment(SpeakerId SpeakerId, string Text, long StartMs, long EndMs);

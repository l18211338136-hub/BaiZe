using System.Collections.Generic;

namespace BaiZe.Domain.Meetings;

/// <summary>会议（聚合根）：拥有转写段落与说话人集合，封装会话内声纹聚类不变量。</summary>
public sealed class Meeting
{
    private readonly List<TranscriptSegment> _segments = new();
    private readonly List<Speaker> _speakers = new();

    public Guid Id { get; }
    public string Title { get; }
    public DateTime CreatedAt { get; }
    public MeetingStatus Status { get; private set; } = MeetingStatus.Created;
    public IReadOnlyList<TranscriptSegment> Segments => _segments;
    public IReadOnlyList<Speaker> Speakers => _speakers;
    public string? Summary { get; private set; }

    public Meeting(Guid id, string title)
    {
        Id = id;
        Title = title;
        CreatedAt = DateTime.UtcNow;
    }

    public void Start() => Status = MeetingStatus.Recording;
    public void Complete() => Status = MeetingStatus.Completed;
    public void SetSummary(string summary) => Summary = summary;

    /// <summary>按声纹归并到已知/相似说话人，保证同一人在会话内只出现一个 Speaker。</summary>
    public Speaker GetOrAddSpeaker(SpeakerId id, float[] voiceprint)
    {
        var byId = _speakers.FirstOrDefault(s => s.Id == id);
        if (byId is not null) return byId;

        var similar = _speakers.FirstOrDefault(s => VoiceprintMath.Cosine(s.Voiceprint, voiceprint) >= 0.8f);
        if (similar is not null) return similar;

        var sp = new Speaker(id, voiceprint);
        _speakers.Add(sp);
        return sp;
    }

    public void AddSegment(TranscriptSegment segment) => _segments.Add(segment);
}

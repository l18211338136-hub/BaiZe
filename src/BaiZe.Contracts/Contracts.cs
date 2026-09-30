namespace BaiZe.Contracts;

/// <summary>
/// 说话人信息：声纹 ID 绑定用户档案（跨会议复用，会议室据此显示"谁在说话"）。
/// 前后端共享，零依赖。
/// </summary>
public sealed record SpeakerInfo(string SpeakerId, string? DisplayName, string? AvatarUrl);

/// <summary>字幕段落 DTO（前端实时渲染用）。</summary>
public sealed record TranscriptSegmentDto(string SpeakerId, string Text, long StartMs, long EndMs);

public enum MeetingStatus { Created, Recording, Completed }

/// <summary>会议视图模型（给前端 / 未来 REST 接口）。</summary>
public sealed record MeetingDto(
    Guid Id,
    string Title,
    DateTime CreatedAt,
    MeetingStatus Status,
    IReadOnlyList<TranscriptSegmentDto> Segments,
    IReadOnlyList<SpeakerInfo> Speakers,
    string? Summary);

/// <summary>语音指令 DTO。</summary>
public sealed record VoiceCommandDto(Guid Id, string RawText, string? Intent, string? ResultJson, DateTime CreatedAt);

/// <summary>指令执行结果 DTO。</summary>
public sealed record CommandResultDto(bool Success, string? Message, string? DataJson);

/// <summary>领域事件 DTO（跨进程 / 接口传递用，与 Domain 事件一一对应）。</summary>
public sealed record MeetingTranscribedEvent(Guid MeetingId, DateTime At);
public sealed record CommandExecutedEvent(Guid CommandId, string Intent, bool Success, DateTime At);

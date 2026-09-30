using BaiZe.Contracts;
using BaiZe.Domain.Commands;
using BaiZe.Domain.Meetings;

namespace BaiZe.Domain.Events;

/// <summary>会议转写完成：驱动纪要生成（最终一致性）。实现 <see cref="IDomainEvent"/>。</summary>
public sealed record MeetingTranscribed(Meeting Meeting) : IDomainEvent;

/// <summary>指令执行完成：驱动审计日志（谁/何时/意图/参数/结果，可回放追责）。实现 <see cref="IDomainEvent"/>。</summary>
public sealed record CommandExecuted(VoiceCommand Command, ExecutionResult Result) : IDomainEvent;

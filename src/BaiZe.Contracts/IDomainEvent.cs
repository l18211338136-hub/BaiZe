namespace BaiZe.Contracts;

/// <summary>
/// 领域事件标记接口。进程内经 <c>BaiZe.Mediator</c> 的 <c>Publish</c> 分发的事件消息实现此接口，
/// 以与「请求/响应」命令消息区分。仅作约定与未来扩展（如 Outbox / 事件溯源）之用。
/// <para>
/// 进程内场景直接承载 Domain 实体（如 <c>Meeting</c> / <c>VoiceCommand</c>），零序列化；
/// 跨进程 / 接口传递时再映射为 Contracts 中的 <c>*Event</c> DTO（如 <see cref="MeetingTranscribedEvent"/>）。
/// </para>
/// </summary>
public interface IDomainEvent
{
}

namespace BaiZe.Domain.Meetings;

/// <summary>
/// 会议状态（领域枚举）。与 <see cref="BaiZe.Contracts.MeetingStatus"/> 的值一一对应，
/// 由 Application 层在 Map 时通过 int 中间量转换，从而保持 Domain 对 Contracts 零依赖。
/// </summary>
public enum MeetingStatus
{
    Created,
    Recording,
    Completed
}

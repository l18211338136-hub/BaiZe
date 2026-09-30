namespace BaiZe.Domain.Meetings;

/// <summary>说话人标识（值对象）。声纹分段出的局部编号不直接使用，靠 SpeakerId 归并到同一人。</summary>
public readonly record struct SpeakerId(Guid Value)
{
    public static SpeakerId New() => new(Guid.NewGuid());
}

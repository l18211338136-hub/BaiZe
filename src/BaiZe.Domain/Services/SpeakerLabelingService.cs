using BaiZe.Domain.Meetings;
using BaiZe.Domain.Ports;

namespace BaiZe.Domain.Services;

/// <summary>
/// 说话人标注服务：把 ASR 原始段按声纹匹配到已知/新说话人。
/// 已知（跨会议注册过）→ 直接显示其用户名；未知 → 标记为"新声纹"，提示绑定。
/// </summary>
public sealed class SpeakerLabelingService
{
    private readonly ISpeakerRegistry _registry;

    public SpeakerLabelingService(ISpeakerRegistry registry) => _registry = registry;

    public async Task<Speaker> LabelAsync(Meeting meeting, RawSegment raw, CancellationToken ct = default)
    {
        var known = await _registry.FindByVoiceprintAsync(raw.Voiceprint, ct);
        if (known is not null)
        {
            // 复用已注册声纹（跨会议认得"小明"）
            return meeting.GetOrAddSpeaker(known.Id, raw.Voiceprint);
        }

        // 会话内未注册的新声纹：创建并提示用户绑定
        return meeting.GetOrAddSpeaker(SpeakerId.New(), raw.Voiceprint);
    }
}

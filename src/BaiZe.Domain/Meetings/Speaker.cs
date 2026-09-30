namespace BaiZe.Domain.Meetings;

/// <summary>说话人（实体）：局部声纹 + 绑定的用户档案。同一声纹跨会议复用。</summary>
public sealed class Speaker
{
    public SpeakerId Id { get; }
    public float[] Voiceprint { get; }
    public UserInfo? User { get; private set; }

    public Speaker(SpeakerId id, float[] voiceprint, UserInfo? user = null)
    {
        Id = id;
        Voiceprint = voiceprint;
        User = user;
    }

    /// <summary>注册/修正绑定：把声纹与"小明"这类用户档案关联。</summary>
    public void BindUser(UserInfo user) => User = user;
}

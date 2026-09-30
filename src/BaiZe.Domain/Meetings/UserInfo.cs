namespace BaiZe.Domain.Meetings;

/// <summary>用户档案（值对象）：声纹绑定的显示名与头像。会议室据此显示"谁在说话"。</summary>
public sealed record UserInfo(string DisplayName, string? AvatarUrl = null);

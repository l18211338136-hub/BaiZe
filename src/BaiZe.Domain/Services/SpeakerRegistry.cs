using BaiZe.Domain.Meetings;

namespace BaiZe.Domain.Services;

/// <summary>声纹注册表端口：持久化"声纹向量 ↔ 用户档案"映射，跨会议复用。</summary>
public interface ISpeakerRegistry
{
    /// <summary>按声纹相似度查找已知说话人（阈值命中则返回，否则 null）。</summary>
    Task<Speaker?> FindByVoiceprintAsync(float[] voiceprint, CancellationToken ct = default);

    Task RegisterAsync(Speaker speaker, CancellationToken ct = default);
}

/// <summary>默认注册表（内存桩）：首版放内存；后续由 SQLite/向量库实现。</summary>
public sealed class SpeakerRegistry : ISpeakerRegistry
{
    private readonly List<Speaker> _known = new();
    private const float Threshold = 0.75f;

    public Task<Speaker?> FindByVoiceprintAsync(float[] vp, CancellationToken ct = default)
    {
        Speaker? best = null;
        float bestScore = -1f;
        foreach (var s in _known)
        {
            var score = VoiceprintMath.Cosine(s.Voiceprint, vp);
            if (score > bestScore) { bestScore = score; best = s; }
        }
        return Task.FromResult(bestScore >= Threshold ? best : null);
    }

    public Task RegisterAsync(Speaker speaker, CancellationToken ct = default)
    {
        _known.Add(speaker);
        return Task.CompletedTask;
    }
}

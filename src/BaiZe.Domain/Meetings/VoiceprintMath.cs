namespace BaiZe.Domain.Meetings;

/// <summary>声纹向量相似度工具（余弦），用于跨会议/会话内声纹归并。</summary>
public static class VoiceprintMath
{
    public static float Cosine(float[] a, float[] b)
    {
        if (a.Length == 0 || b.Length == 0 || a.Length != b.Length) return 0f;
        float dot = 0f, na = 0f, nb = 0f;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }
        return (na == 0f || nb == 0f) ? 0f : dot / (MathF.Sqrt(na) * MathF.Sqrt(nb));
    }
}

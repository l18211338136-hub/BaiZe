using BaiZe.Domain.Commands;
using BaiZe.Domain.Ports;

namespace BaiZe.Domain.Services;

/// <summary>意图解析服务：调用 LLM(function calling) 把自然语言文本解析为意图 + 参数。</summary>
public sealed class IntentResolutionService
{
    private readonly ILlmGateway _llm;

    public IntentResolutionService(ILlmGateway llm) => _llm = llm;

    public async Task<(Intent Intent, ToolArgs Args)> ResolveAsync(
        string text, IReadOnlyList<ITool> tools, CancellationToken ct = default)
    {
        var resolution = await _llm.ResolveIntentAsync(text, tools, ct);
        return (resolution.Intent, resolution.Args);
    }
}

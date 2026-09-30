using BaiZe.Domain.Commands;
using BaiZe.Domain.Ports;
using System.Text.Json.Nodes;

namespace BaiZe.Infrastructure.Llm;

/// <summary>
/// 第三方 OpenAI 兼容 LLM 网关（桩）。真实实现：调用配置的 base_url /v1/chat/completions，
/// 走 function calling 返回结构化意图与参数。
/// </summary>
public sealed class LlmGateway : ILlmGateway
{
    public Task<IntentResolution> ResolveIntentAsync(string text, IReadOnlyList<ITool> tools, CancellationToken ct = default) =>
        Task.FromResult(new IntentResolution(new Intent("unknown", RiskLevel.Low), new ToolArgs(new JsonObject())));

    public Task<string> SummarizeAsync(string transcript, CancellationToken ct = default) =>
        Task.FromResult(string.Empty);
}

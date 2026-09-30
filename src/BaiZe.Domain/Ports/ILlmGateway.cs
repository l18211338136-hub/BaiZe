using BaiZe.Domain.Commands;
using System.Text.Json.Nodes;

namespace BaiZe.Domain.Ports;

/// <summary>LLM 解析结果：意图 + 结构化参数。</summary>
public sealed record IntentResolution(Intent Intent, ToolArgs Args);

/// <summary>LLM 网关端口：第三方 OpenAI 兼容接口。支持 function calling。</summary>
public interface ILlmGateway
{
    /// <summary>把文本 + 可用工具 schema 给模型，返回结构化意图与参数。</summary>
    Task<IntentResolution> ResolveIntentAsync(string text, IReadOnlyList<ITool> tools, CancellationToken ct = default);

    /// <summary>会议纪要与总结。</summary>
    Task<string> SummarizeAsync(string transcript, CancellationToken ct = default);
}

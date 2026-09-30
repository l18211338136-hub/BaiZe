using BaiZe.Domain.Commands;
using System.Text.Json.Nodes;

namespace BaiZe.Domain.Ports;

/// <summary>业务工具端口（agent tool registry）：语音指令最终落到这里执行。可插拔。</summary>
public interface ITool
{
    /// <summary>工具名，对应 LLM 解析出的 intent.Name。</summary>
    string Name { get; }

    /// <summary>给 LLM 看的工具说明（function calling description）。</summary>
    string Description { get; }

    /// <summary>风险等级，驱动全自动执行护栏。</summary>
    RiskLevel Risk { get; }

    /// <summary>function calling 参数 JSON Schema。</summary>
    JsonObject ParameterSchema { get; }

    Task<ExecutionResult> ExecuteAsync(ToolArgs args, CancellationToken ct = default);
}

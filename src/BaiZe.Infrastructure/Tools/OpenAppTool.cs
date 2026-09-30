using BaiZe.Domain.Commands;
using BaiZe.Domain.Ports;
using System.Text.Json.Nodes;

namespace BaiZe.Infrastructure.Tools;

/// <summary>打开本地应用/文件（低风险 PC 控制工具）。</summary>
public sealed class OpenAppTool : ITool
{
    public string Name => "open_app";
    public string Description => "Open a local application or file by path.";
    public RiskLevel Risk => RiskLevel.Low;
    public JsonObject ParameterSchema => JsonNode.Parse(
        """{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]}""")!.AsObject();

    public Task<ExecutionResult> ExecuteAsync(ToolArgs args, CancellationToken ct = default) =>
        Task.FromResult(new ExecutionResult(true, "opened (stub)"));
}

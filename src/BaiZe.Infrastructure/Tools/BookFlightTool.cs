using BaiZe.Domain.Commands;
using BaiZe.Domain.Ports;
using System.Text.Json.Nodes;

namespace BaiZe.Infrastructure.Tools;

/// <summary>订机票（高风险工具）。全自动执行时由 ExecutionPolicy 默认 DryRun 或白名单放行。</summary>
public sealed class BookFlightTool : ITool
{
    public string Name => "book_flight";
    public string Description => "Book a flight. Requires real booking backend auth.";
    public RiskLevel Risk => RiskLevel.High;
    public JsonObject ParameterSchema => JsonNode.Parse(
        """{"type":"object","properties":{"from":{"type":"string"},"to":{"type":"string"},"date":{"type":"string"}},"required":["from","to","date"]}""")!.AsObject();

    public Task<ExecutionResult> ExecuteAsync(ToolArgs args, CancellationToken ct = default) =>
        Task.FromResult(new ExecutionResult(true, "booked (stub)"));
}

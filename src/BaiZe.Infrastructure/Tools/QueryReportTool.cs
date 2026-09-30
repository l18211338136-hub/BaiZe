using BaiZe.Domain.Commands;
using BaiZe.Domain.Ports;
using System.Text.Json.Nodes;

namespace BaiZe.Infrastructure.Tools;

/// <summary>查询报表/数据仓库（中风险工具）。</summary>
public sealed class QueryReportTool : ITool
{
    public string Name => "query_report";
    public string Description => "Query the reporting / data-warehouse system.";
    public RiskLevel Risk => RiskLevel.Medium;
    public JsonObject ParameterSchema => JsonNode.Parse(
        """{"type":"object","properties":{"sql":{"type":"string"}},"required":["sql"]}""")!.AsObject();

    public Task<ExecutionResult> ExecuteAsync(ToolArgs args, CancellationToken ct = default) =>
        Task.FromResult(new ExecutionResult(true, "queried (stub)"));
}

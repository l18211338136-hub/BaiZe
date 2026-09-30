using BaiZe.Domain.Commands;
using BaiZe.Domain.Policies;
using BaiZe.Domain.Ports;

namespace BaiZe.Domain.Services;

/// <summary>
/// 指令分发器（全自动、无确认框）：解析出意图后直接路由并执行 ITool，
/// 由 ExecutionPolicy 裁决 Allow / Deny / DryRun。执行结果回写聚合。
/// </summary>
public sealed class CommandDispatcher
{
    private readonly ITool[] _tools;
    private readonly IExecutionPolicy _policy;

    public CommandDispatcher(IEnumerable<ITool> tools, IExecutionPolicy policy)
    {
        _tools = tools.ToArray();
        _policy = policy;
    }

    public async Task<ExecutionResult> DispatchAsync(VoiceCommand cmd, CancellationToken ct = default)
    {
        var tool = _tools.FirstOrDefault(t => t.Name == cmd.ResolvedIntent?.Name);
        if (tool is null)
        {
            // 意图无法映射到任何已注册工具（例如 LLM 解析为 unknown、或工具未注册）。
            // 这是「助手没听懂指令」的正常业务状态，应返回用户友好的失败结果，而非抛出未处理异常炸掉整个 UI。
            var unmatched = new ExecutionResult(false,
                $"未识别的指令：'{cmd.ResolvedIntent?.Name}'。可能缺少对应工具，或指令表述暂不被理解。");
            cmd.ApplyResult(unmatched);
            return unmatched;
        }

        var decision = _policy.Decide(tool, cmd.Args!);
        var result = decision switch
        {
            ExecutionDecision.Deny => new ExecutionResult(false, "Blocked by execution policy."),
            ExecutionDecision.DryRun => new ExecutionResult(true, $"[DryRun] would execute '{tool.Name}' with {cmd.Args!.Values}"),
            _ => await tool.ExecuteAsync(cmd.Args!, ct)
        };

        cmd.ApplyResult(result);
        return result;
    }
}

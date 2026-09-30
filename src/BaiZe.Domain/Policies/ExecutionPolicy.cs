using BaiZe.Domain.Commands;
using BaiZe.Domain.Ports;

namespace BaiZe.Domain.Policies;

/// <summary>全自动执行裁决：不弹 UI，由领域策略决定 Allow / Deny / DryRun。</summary>
public enum ExecutionDecision { Allow, Deny, DryRun }

public interface IExecutionPolicy
{
    ExecutionDecision Decide(ITool tool, ToolArgs args);
}

/// <summary>
/// 默认护栏：高风险（真实下单等）默认 DryRun 或需白名单放行；
/// 首版默认 DryRun=true 验证 ITool 路由正确，再切 prod 全自动。
/// </summary>
public sealed class DefaultExecutionPolicy : IExecutionPolicy
{
    private readonly bool _dryRunByDefault;
    private readonly HashSet<string> _allowList;

    public DefaultExecutionPolicy(bool dryRunByDefault = true, HashSet<string>? allowList = null)
    {
        _dryRunByDefault = dryRunByDefault;
        _allowList = allowList ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    public ExecutionDecision Decide(ITool tool, ToolArgs args)
    {
        if (tool.Risk == RiskLevel.High && !_allowList.Contains(tool.Name))
            return _dryRunByDefault ? ExecutionDecision.DryRun : ExecutionDecision.Deny;
        return ExecutionDecision.Allow;
    }
}

namespace BaiZe.Domain.Commands;

/// <summary>意图（值对象）：由 LLM 解析出的"要做什么"以及对应风险。</summary>
public sealed record Intent(string Name, RiskLevel Risk);

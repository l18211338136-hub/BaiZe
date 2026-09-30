using System.Text.Json.Nodes;

namespace BaiZe.Domain.Commands;

/// <summary>执行结果（值对象）：工具执行后的成功状态、消息与可选数据。</summary>
public sealed record ExecutionResult(bool Success, string? Message, JsonObject? Data = null);

using System.Text.Json.Nodes;

namespace BaiZe.Domain.Commands;

/// <summary>工具参数（值对象）：LLM 解析出的结构化参数，以 JSON 承载。</summary>
public sealed record ToolArgs(JsonObject Values);

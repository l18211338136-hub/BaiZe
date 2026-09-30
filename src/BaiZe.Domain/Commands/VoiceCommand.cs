using System.Text.Json.Nodes;

namespace BaiZe.Domain.Commands;

/// <summary>语音指令（聚合根）：原始文本 → 解析意图+参数 → 应用执行结果。</summary>
public sealed class VoiceCommand
{
    public Guid Id { get; }
    public string RawText { get; }
    public Intent? ResolvedIntent { get; private set; }
    public ToolArgs? Args { get; private set; }
    public ExecutionResult? Result { get; private set; }
    public DateTime CreatedAt { get; }

    public VoiceCommand(Guid id, string rawText)
    {
        Id = id;
        RawText = rawText;
        CreatedAt = DateTime.UtcNow;
    }

    public void Resolve(Intent intent, ToolArgs args)
    {
        ResolvedIntent = intent;
        Args = args;
    }

    public void ApplyResult(ExecutionResult result) => Result = result;
}

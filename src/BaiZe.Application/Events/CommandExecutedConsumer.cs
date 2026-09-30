using BaiZe.Contracts;
using BaiZe.Domain.Commands;
using BaiZe.Domain.Events;
using BaiZe.Mediator;
using Microsoft.Extensions.Logging;

namespace BaiZe.Application.Events;

/// <summary>
/// <see cref="CommandExecuted"/> 领域事件订阅者：写审计日志（谁/何时/意图/参数/结果，可回放追责）。
/// 由 <c>ExecuteVoiceCommandConsumer</c> 在处理完成、回包后通过 <c>context.Publish</c> 触发。
/// <para>
/// 事件可无人订阅（no-op）。此处为「最终一致性」落地示例；批5 可改为落审计表 / 发往审计服务。
/// 订阅者务必保持健壮：本订阅者只记日志、不抛异常，避免连累主命令（命令已回包、已落库）。
/// </para>
/// </summary>
public sealed class CommandExecutedConsumer : IConsumer<CommandExecuted>
{
    private readonly ILogger<CommandExecutedConsumer> _logger;

    public CommandExecutedConsumer(ILogger<CommandExecutedConsumer> logger) => _logger = logger;

    public Task Consume(ConsumeContext<CommandExecuted> context)
    {
        var e = context.Message;
        _logger.LogInformation(
            "领域事件 CommandExecuted: 指令={RawText} 意图={Intent} 成功={Success} 消息={Message}",
            e.Command.RawText,
            e.Command.ResolvedIntent?.Name ?? "<未解析>",
            e.Result.Success,
            e.Result.Message);

        return Task.CompletedTask;
    }
}

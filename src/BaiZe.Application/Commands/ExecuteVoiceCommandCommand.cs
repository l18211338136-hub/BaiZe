using BaiZe.Contracts;
using BaiZe.Domain.Commands;
using BaiZe.Domain.Events;
using BaiZe.Domain.Ports;
using BaiZe.Domain.Repositories;
using BaiZe.Domain.Services;
using BaiZe.Mediator;

namespace BaiZe.Application.Commands;

/// <summary>执行语音指令命令（全自动、无确认框）。</summary>
public sealed record ExecuteVoiceCommandCommand(string RawText);

public sealed class ExecuteVoiceCommandConsumer : IConsumer<ExecuteVoiceCommandCommand>
{
    private readonly IntentResolutionService _resolver;
    private readonly CommandDispatcher _dispatcher;
    private readonly ICommandRepository _repo;
    private readonly IEnumerable<ITool> _tools;

    public ExecuteVoiceCommandConsumer(
        IntentResolutionService resolver,
        CommandDispatcher dispatcher,
        ICommandRepository repo,
        IEnumerable<ITool> tools)
    {
        _resolver = resolver;
        _dispatcher = dispatcher;
        _repo = repo;
        _tools = tools;
    }

    public async Task Consume(ConsumeContext<ExecuteVoiceCommandCommand> context)
    {
        var req = context.Message;
        var cmd = new VoiceCommand(Guid.NewGuid(), req.RawText);
        var (intent, args) = await _resolver.ResolveAsync(req.RawText, _tools.ToList(), context.CancellationToken);
        cmd.Resolve(intent, args);

        var result = await _dispatcher.DispatchAsync(cmd, context.CancellationToken);
        await _repo.SaveAsync(cmd, context.CancellationToken);

        await context.RespondAsync(new CommandResultDto(result.Success, result.Message, result.Data?.ToJsonString()));

        // 命令副作用已完成、响应已回包后再发领域事件（最终一致性）：
        // 驱动审计日志（CommandExecutedConsumer）。事件可无人订阅，订阅者异常不应连累主命令。
        await context.Publish(new CommandExecuted(cmd, result));
    }
}

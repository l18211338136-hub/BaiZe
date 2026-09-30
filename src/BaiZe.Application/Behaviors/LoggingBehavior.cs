using BaiZe.Mediator;
using Microsoft.Extensions.Logging;

namespace BaiZe.Application.Behaviors;

/// <summary>
/// 管线行为示例：横切日志。记录每个消息（命令请求 / 领域事件）的处理耗时。
/// 注册见 <c>Application/DependencyInjection</c> 的 <c>cfg.AddBehavior&lt;LoggingBehavior&gt;()</c>。
/// 业务 Consumer 保持纯净，日志这类横切关注点统一在管线层处理——这是 CQRS 中介者的核心扩展点。
/// 命令与事件共用同一套行为链（<see cref="MediatorDispatch"/>），保证横切逻辑一致。
/// </summary>
public sealed class LoggingBehavior : IPipelineBehavior
{
    private readonly ILogger<LoggingBehavior> _logger;

    public LoggingBehavior(ILogger<LoggingBehavior> logger) => _logger = logger;

    public async Task Handle(ConsumeContext context, Func<Task> next)
    {
        var messageName = context.Message?.GetType().Name ?? "<null>";
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await next();
        }
        finally
        {
            sw.Stop();
            _logger.LogInformation(
                "Mediator 管线: 消息 {Message} 处理完成, 耗时 {Elapsed}ms, CorrelationId={CorrelationId}",
                messageName, sw.ElapsedMilliseconds, context.CorrelationId);
        }
    }
}

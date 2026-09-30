using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BaiZe.Mediator;

/// <summary>
/// 进程内中介者实现（作用域单例）。持有当前作用域的 IServiceProvider，用于在请求/事件处理时
/// 从同一作用域解析 Consumer 与其依赖（与 MassTransit 的 scoped mediator 行为一致：Consumer 及依赖共享一个 DI 作用域）。
/// </summary>
internal sealed class Mediator : IMediator
{
    private readonly IServiceProvider _provider;
    private readonly MediatorOptions _options;
    private readonly ILogger<Mediator>? _logger;

    public Mediator(IServiceProvider provider, MediatorOptions options)
    {
        _provider = provider;
        _options = options;
        // 宿主注册了日志（AddLogging）才有实例；纯库环境下为 null，不影响功能。
        _logger = provider.GetService<ILogger<Mediator>>();
    }

    public IRequestClient<TRequest> CreateRequestClient<TRequest>() where TRequest : class
        => new RequestClient<TRequest>(this, _provider, _options);

    /// <summary>强类型发布。按 <c>message.GetType()</c> 运行时类型查找订阅 Consumer 并分发——
    /// 消息以接口/基类静态类型发布（如 IDomainEvent 集合）时也能命中订阅者，不会静默丢失。</summary>
    public Task Publish<TMessage>(TMessage message, CancellationToken cancellationToken = default) where TMessage : class
    {
        if (message is null) throw new ArgumentNullException(nameof(message));
        return PublishCore(message.GetType(), message, cancellationToken);
    }

    /// <summary>
    /// 按运行时具体类型发布。领域事件常以 IDomainEvent 集合持有，需按其真实类型路由到对应 Consumer。
    /// </summary>
    public Task Publish(object message, CancellationToken cancellationToken = default)
    {
        if (message is null) throw new ArgumentNullException(nameof(message));
        return PublishCore(message.GetType(), message, cancellationToken);
    }

    /// <summary>无响应命令：要求恰好一个 Consumer，不要求回包，异常上抛（与 Publish 的订阅者隔离语义相反）。</summary>
    public Task Send<TMessage>(TMessage message, CancellationToken cancellationToken = default) where TMessage : class
    {
        if (message is null) throw new ArgumentNullException(nameof(message));

        var messageType = message.GetType();
        if (!_options.ConsumerMap.TryGetValue(messageType, out var consumerTypes) || consumerTypes.Count == 0)
            throw new InvalidOperationException(
                $"未找到消息 {messageType.Name} 对应的 Consumer，请先在 AddMediator(cfg => cfg.AddConsumer<...>()) 中注册。");
        if (consumerTypes.Count > 1)
            throw new InvalidOperationException(
                $"消息 {messageType.Name} 注册了 {consumerTypes.Count} 个 Consumer（{string.Join("、", consumerTypes.Select(t => t.Name))}）。" +
                "Send 要求恰好一个 Consumer；多订阅者请用 Publish 事件语义。");

        return Dispatch(messageType, message, consumerTypes, cancellationToken,
            isolateExceptions: false, correlationId: null);
    }

    /// <summary>宿主内部嵌套发布入口：Consumer 经 context.Publish 发布的事件继承发布方的 CorrelationId，
    /// 使「命令 → 事件 → 后续消费」在日志里串成一条链。</summary>
    internal Task PublishInternal(object message, Guid? correlationId, CancellationToken cancellationToken)
    {
        if (message is null) throw new ArgumentNullException(nameof(message));
        return PublishCore(message.GetType(), message, cancellationToken,
            isolateExceptions: true, correlationId: correlationId);
    }

    private Task PublishCore(Type messageType, object message, CancellationToken cancellationToken,
        bool isolateExceptions = true, Guid? correlationId = null)
    {
        if (!_options.ConsumerMap.TryGetValue(messageType, out var consumerTypes) || consumerTypes.Count == 0)
            return Task.CompletedTask; // 事件可无人订阅（no-op），合法

        return Dispatch(messageType, message, consumerTypes, cancellationToken, isolateExceptions, correlationId);
    }

    /// <summary>泛型分发 MethodInfo 按消息类型缓存：每个类型只 MakeGenericMethod 一次（进程级）。</summary>
    private static readonly ConcurrentDictionary<Type, MethodInfo> DispatchMethodCache = new();

    private Task Dispatch(Type messageType, object message, IReadOnlyList<Type> consumerTypes,
        CancellationToken cancellationToken, bool isolateExceptions, Guid? correlationId)
    {
        var method = DispatchMethodCache.GetOrAdd(messageType, static t =>
            typeof(Mediator)
                .GetMethod(nameof(DispatchGeneric), BindingFlags.NonPublic | BindingFlags.Instance)!
                .MakeGenericMethod(t));
        return (Task)method.Invoke(this,
            new object[] { message, consumerTypes, cancellationToken, isolateExceptions, correlationId })!;
    }

    private async Task DispatchGeneric<TMessage>(object message, IReadOnlyList<Type> consumerTypes,
        CancellationToken cancellationToken, bool isolateExceptions, Guid? correlationId)
        where TMessage : class
    {
        var typed = (TMessage)message;

        foreach (var consumerType in consumerTypes)
        {
            try
            {
                var context = new ConsumeContext<TMessage>(typed, cancellationToken)
                {
                    // 顶层派发生成新 ID；嵌套发布（PublishInternal）继承发布方的 ID
                    CorrelationId = correlationId ?? Guid.NewGuid()
                };
                // 注入发布能力：Consumer 处理过程中可经 context.Publish 发布领域事件（继承当前 CorrelationId）。
                context.PublishDelegate = (msg, ct) => PublishInternal(msg, context.CorrelationId, ct);
                var consumer = (IConsumer<TMessage>)_provider.GetRequiredService(consumerType);
                var terminal = (Func<Task>)(() => consumer.Consume(context));
                var pipeline = MediatorDispatch.BuildPipeline(context, _provider, _options.BehaviorTypes, terminal);
                await pipeline();
            }
            catch (Exception ex)
            {
                // Publish：单个订阅者失败（含 DI 解析失败）不中断其余订阅者，也不连累发布方。
                // Send：异常照常上抛——调用方需要知道命令没执行成功。
                if (!isolateExceptions) throw;
                _logger?.LogWarning(ex, "Consumer {Consumer} 处理事件 {Event} 失败",
                    consumerType.Name, typeof(TMessage).Name);
            }
        }
    }
}

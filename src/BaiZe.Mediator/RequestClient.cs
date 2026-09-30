using Microsoft.Extensions.DependencyInjection;

namespace BaiZe.Mediator;

/// <summary>
/// 请求/响应客户端实现。调用 GetResponse 时：构造消费上下文 → 从作用域解析 Consumer →
/// 用注册的管线行为包裹（先注册的外层优先）→ 执行 → 读取 Consumer 的 RespondAsync 回包 → 返回。
/// 全程同进程、按引用传递，无序列化、无 broker。
/// </summary>
internal sealed class RequestClient<TRequest> : IRequestClient<TRequest> where TRequest : class
{
    private readonly Mediator _mediator;
    private readonly IServiceProvider _provider;
    private readonly MediatorOptions _options;

    public RequestClient(Mediator mediator, IServiceProvider provider, MediatorOptions options)
    {
        _mediator = mediator;
        _provider = provider;
        _options = options;
    }

    public async Task<Response<TResponse>> GetResponse<TResponse>(TRequest request, CancellationToken cancellationToken = default)
    {
        if (!_options.ConsumerMap.TryGetValue(typeof(TRequest), out var consumerTypes) || consumerTypes.Count == 0)
            throw new InvalidOperationException(
                $"未找到消息 {typeof(TRequest).Name} 对应的 Consumer，请先在 AddMediator(cfg => cfg.AddConsumer<...>()) 中注册。");

        // 请求/响应要求恰好一个 Consumer：误注册多个时立即报错，而不是静默走第一个（否则另一个订阅者永远收不到消息）。
        if (consumerTypes.Count > 1)
            throw new InvalidOperationException(
                $"请求 {typeof(TRequest).Name} 注册了 {consumerTypes.Count} 个 Consumer（{string.Join("、", consumerTypes.Select(t => t.Name))}）。" +
                "请求/响应场景要求恰好一个 Consumer；多订阅者请走 Publish 事件语义。");

        var consumerType = consumerTypes[0];

        var context = new ConsumeContext<TRequest>(request, cancellationToken)
        {
            CorrelationId = Guid.NewGuid() // 请求链的追踪起点；Consumer 发布的事件将继承此 ID
        };
        // 注入发布能力：命令 Consumer 处理过程中可经 context.Publish 发布领域事件（继承 CorrelationId）。
        context.PublishDelegate = (msg, ct) => _mediator.PublishInternal(msg, context.CorrelationId, ct);

        // 终端：解析并调用 Consumer；复用共享管线装配（与事件发布同一套行为链）包裹横切逻辑。
        var consumer = (IConsumer<TRequest>)_provider.GetRequiredService(consumerType);
        var terminal = (Func<Task>)(() => consumer.Consume(context));
        var pipeline = MediatorDispatch.BuildPipeline(context, _provider, _options.BehaviorTypes, terminal);

        await pipeline();

        if (!context.HasResponded)
            throw new InvalidOperationException(
                $"Consumer {consumerType.Name} 处理 {typeof(TRequest).Name} 时未调用 RespondAsync 回包。");

        if (context.ResponseMessage is null)
            throw new InvalidOperationException(
                $"Consumer {consumerType.Name} 回包为 null（RespondAsync 传入了 null），期望 {typeof(TResponse).Name}。");

        if (context.ResponseMessage is not TResponse typed)
            throw new InvalidOperationException(
                $"响应类型不匹配：期望 {typeof(TResponse).Name}，实际 {context.ResponseMessage.GetType().Name}。");

        return new Response<TResponse>(typed);
    }
}

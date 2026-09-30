namespace BaiZe.Mediator;

/// <summary>
/// 消费上下文（非泛型基类），由中介者构造并传入 Consumer。
/// 镜像 MassTransit 的 ConsumeContext：承载消息、取消令牌，并提供 RespondAsync 回包与 Publish 发布领域事件。
/// 设计为基类 + 泛型子类，是为了既能在 Consumer 端给出强类型 Message，
/// 又能在管线行为（IPipelineBehavior）端用非泛型上下文统一包裹。
/// </summary>
public abstract class ConsumeContext
{
    public CancellationToken CancellationToken { get; }

    /// <summary>原始消息（object 视图）。管线行为（IPipelineBehavior）统一通过此视图访问消息。</summary>
    public object? Message { get; }

    /// <summary>
    /// 链路追踪 ID：顶层派发时生成；Consumer 经 context.Publish 发布的事件**继承**发布方的 ID，
    /// 使「命令 → 领域事件 → 后续消费」整条链在日志中可串联（如审计日志按此聚合）。
    /// </summary>
    public Guid CorrelationId { get; internal set; }

    /// <summary>Consumer 是否已通过 RespondAsync 回包。</summary>
    internal bool HasResponded { get; private set; }

    /// <summary>Consumer 回包的消息体（由 RespondAsync 写入）。</summary>
    internal object? ResponseMessage { get; private set; }

    /// <summary>
    /// 发布委托：由调度方（Mediator / RequestClient）在构造上下文后注入。
    /// 使 Consumer 在处理过程中可调用 <see cref="Publish(object, CancellationToken)"/> 发布领域事件，
    /// 实现「命令处理 → 发布事件」的领域事件模式（镜像 MassTransit 的 context.Publish）。
    /// </summary>
    internal Func<object, CancellationToken, Task>? PublishDelegate { get; set; }

    protected ConsumeContext(object? message, CancellationToken cancellationToken)
    {
        Message = message;
        CancellationToken = cancellationToken;
    }

    /// <summary>回包：把响应消息交给等待中的请求方。重复调用会抛异常。</summary>
    public Task RespondAsync(object? response)
    {
        if (HasResponded)
            throw new InvalidOperationException("已调用过 RespondAsync，不能对同一请求重复响应。");
        ResponseMessage = response;
        HasResponded = true;
        return Task.CompletedTask;
    }

    /// <summary>
    /// 发布领域事件（按运行时具体类型路由到订阅 Consumer）。可在 Consumer 处理过程中调用。
    /// 领域事件常以 IDomainEvent 集合形式持有，需按真实类型路由到对应 Consumer。
    /// 若当前上下文未注入发布能力（PublishDelegate 为空）则抛异常。
    /// </summary>
    public Task Publish(object message, CancellationToken cancellationToken = default)
    {
        if (PublishDelegate is null)
            throw new InvalidOperationException(
                "当前 ConsumeContext 未注入发布能力（PublishDelegate 为空），无法 Publish。请确认上下文由 Mediator 调度创建。");
        return PublishDelegate(message, cancellationToken);
    }
}

/// <summary>
/// 强类型消费上下文。Consumer 通过 <see cref="Message"/> 拿到强类型请求，
/// 通过 <see cref="RespondAsync{TResponse}"/> 回强类型响应，并可经 <see cref="Publish{TEvent}"/> 发布领域事件。
/// </summary>
public sealed class ConsumeContext<TMessage> : ConsumeContext where TMessage : class
{
    public ConsumeContext(TMessage message, CancellationToken cancellationToken = default)
        : base(message, cancellationToken) { }

    /// <summary>强类型请求消息（隐藏基类 object? Message，提供编译期类型安全）。</summary>
    public new TMessage Message => (TMessage)base.Message!;

    /// <summary>回强类型响应消息。注意：必须显式 base.RespondAsync，否则会经重载决议自递归（派生泛型优先于基类非泛型）。</summary>
    public Task RespondAsync<TResponse>(TResponse response) => base.RespondAsync((object?)response);

    /// <summary>发布领域事件（强类型重载）。必须以 base. 转发，否则会与基类 Publish 发生同样的自递归陷阱。</summary>
    public Task Publish<TEvent>(TEvent message, CancellationToken cancellationToken = default) where TEvent : class
        => base.Publish(message!, cancellationToken);
}

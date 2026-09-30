namespace BaiZe.Mediator;

/// <summary>
/// 作用域中介者（进程内）。表现层/门面持有它：
/// - 通过 CreateRequestClient 发起请求/响应调用（一个 Consumer 处理并返回响应）；
/// - 通过 Publish 发布领域事件（一个或多个订阅者处理，无响应）。
/// 镜像 MassTransit 的 IScopedMediator / IPublishEndpoint。
/// </summary>
public interface IScopedMediator
{
    /// <summary>为某请求消息类型创建一个请求客户端。</summary>
    IRequestClient<TRequest> CreateRequestClient<TRequest>() where TRequest : class;

    /// <summary>
    /// 发送无响应命令：要求恰好一个 Consumer，<b>不要求回包</b>，异常上抛给调用方。
    /// 适合"只触发、不关心返回值"的命令语义；需要响应走 CreateRequestClient，多订阅者走 Publish。
    /// </summary>
    Task Send<TMessage>(TMessage message, CancellationToken cancellationToken = default) where TMessage : class;

    /// <summary>
    /// 发布事件：分发给所有订阅该消息类型的 Consumer（无响应、可无人订阅，无人订阅时为 no-op）。
    /// 用于领域事件（如 MeetingTranscribed / CommandExecuted）的最终一致性处理。
    /// 订阅者异常被隔离（记日志、不影响其余订阅者和发布方）。
    /// </summary>
    Task Publish<TMessage>(TMessage message, CancellationToken cancellationToken = default) where TMessage : class;

    /// <summary>
    /// 按运行时具体类型发布（领域事件常以 IDomainEvent 集合形式持有，需按真实类型路由到订阅 Consumer）。
    /// </summary>
    Task Publish(object message, CancellationToken cancellationToken = default);
}

/// <summary>
/// 中介者根接口（继承 IScopedMediator）。镜像 MassTransit 的 IMediator。
/// </summary>
public interface IMediator : IScopedMediator
{
}

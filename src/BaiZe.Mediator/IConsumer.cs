namespace BaiZe.Mediator;

/// <summary>
/// 消费者（消息处理器）。每个命令/查询消息对应一个 Consumer，
/// 镜像 MassTransit 的 IConsumer&lt;T&gt;：同一套 Consumer 模型，未来若要切换到真实消息总线（RabbitMQ/ASB）也无需改写。
/// </summary>
public interface IConsumer<TMessage> where TMessage : class
{
    /// <summary>处理请求并（通常）调用 context.RespondAsync 回包。</summary>
    Task Consume(ConsumeContext<TMessage> context);
}

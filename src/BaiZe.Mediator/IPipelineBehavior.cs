namespace BaiZe.Mediator;

/// <summary>
/// 管线行为（中间件）。在 Consumer 执行前后横切，用于校验、日志、性能计数、重试、鉴权等。
/// 这是中介者模式的核心扩展点：业务 Consumer 保持纯净，横切关注点统一在管线层处理。
/// 行为按注册顺序外层→内层包裹，先注册的在最外层（最先执行 pre、最后执行 post）。
/// </summary>
public interface IPipelineBehavior
{
    /// <summary>
    /// 处理消息。调用 <paramref name="next"/> 把执行权交给下一个行为或终端 Consumer。
    /// </summary>
    Task Handle(ConsumeContext context, Func<Task> next);
}

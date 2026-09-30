namespace BaiZe.Mediator;

/// <summary>
/// 中介者配置（在 AddMediator(cfg => ...) 中由调用方填充）。
/// 镜像 MassTransit 的 IRegistrationConfigurator 的消费者注册部分。
/// </summary>
public interface IMediatorConfig
{
    /// <summary>注册一个 Consumer（请求消息 → 处理器）。</summary>
    IMediatorConfig AddConsumer<T>() where T : class;

    /// <summary>注册一个管线行为（横切中间件）。</summary>
    IMediatorConfig AddBehavior<T>() where T : class, IPipelineBehavior;
}

internal sealed class MediatorConfig : IMediatorConfig
{
    public List<Type> ConsumerTypes { get; } = new();
    public List<Type> BehaviorTypes { get; } = new();

    public IMediatorConfig AddConsumer<T>() where T : class
    {
        ConsumerTypes.Add(typeof(T));
        return this;
    }

    public IMediatorConfig AddBehavior<T>() where T : class, IPipelineBehavior
    {
        BehaviorTypes.Add(typeof(T));
        return this;
    }
}

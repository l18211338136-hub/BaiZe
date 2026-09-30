using System.Reflection;

namespace BaiZe.Mediator;

/// <summary>
/// 中介者运行期配置（单例）。保存「消息类型 → Consumer 类型列表」映射与管线行为列表。
/// 一条消息可对应多个 Consumer（事件 Publish 场景：一个事件被多个订阅者处理）；
/// 请求/响应场景取其第一个 Consumer（命令/查询通常恰好一个）。
/// 消息类型由 Consumer 实现的 IConsumer&lt;TMessage&gt; 接口在启动时反射推导。
/// </summary>
internal sealed class MediatorOptions
{
    public Dictionary<Type, List<Type>> ConsumerMap { get; }

    public IReadOnlyList<Type> BehaviorTypes { get; }

    public MediatorOptions(IEnumerable<Type> consumerTypes, IEnumerable<Type> behaviorTypes)
    {
        ConsumerMap = new Dictionary<Type, List<Type>>();
        foreach (var consumerType in consumerTypes)
        {
            foreach (var messageType in DeriveMessageTypes(consumerType))
            {
                if (!ConsumerMap.TryGetValue(messageType, out var list))
                {
                    list = new List<Type>();
                    ConsumerMap[messageType] = list;
                }
                list.Add(consumerType);
            }
        }

        BehaviorTypes = behaviorTypes.ToList();
    }

    /// <summary>从 Consumer 类型上实现的全部 IConsumer&lt;TMessage&gt; 接口推导消息类型。
    /// 一个 Consumer 可同时处理多种消息（每个 IConsumer&lt;T&gt; 各注册一条路由），一个都没有则抛异常。</summary>
    private static List<Type> DeriveMessageTypes(Type consumerType)
    {
        var ifaces = consumerType.GetInterfaces()
            .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IConsumer<>))
            .ToList();

        if (ifaces.Count == 0)
            throw new InvalidOperationException(
                $"类型 {consumerType.Name} 未实现 IConsumer<TMessage>，无法注册为 Consumer。");

        return ifaces.Select(i => i.GetGenericArguments()[0]).ToList();
    }
}

using Microsoft.Extensions.DependencyInjection;

namespace BaiZe.Mediator;

/// <summary>
/// 管线装配：用注册的 IPipelineBehavior 包裹终端调用（先注册的外层优先）。
/// 请求/响应与事件发布共用同一套行为链，保证横切逻辑（校验/日志/鉴权）一致。
/// </summary>
internal static class MediatorDispatch
{
    /// <summary>
    /// 构建最终可 await 的管线：把 behaviors 由内到外包裹 terminal。
    /// 列表顺序即注册顺序，反转后先注册者处于最外层（最先执行 pre、最后执行 post）。
    /// </summary>
    internal static Func<Task> BuildPipeline(
        ConsumeContext context,
        IServiceProvider provider,
        IReadOnlyList<Type> behaviorTypes,
        Func<Task> terminal)
    {
        var pipeline = terminal;
        if (behaviorTypes.Count > 0)
        {
            var behaviors = behaviorTypes
                .Select(t => (IPipelineBehavior)provider.GetRequiredService(t))
                .ToList();

            for (var i = behaviors.Count - 1; i >= 0; i--)
            {
                var behavior = behaviors[i];
                var next = pipeline;
                pipeline = () => behavior.Handle(context, next);
            }
        }
        return pipeline;
    }
}

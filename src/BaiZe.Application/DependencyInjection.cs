using BaiZe.Mediator;
using Microsoft.Extensions.DependencyInjection;

namespace BaiZe.Application;

/// <summary>
/// 应用层 DI：注册自研进程内 Mediator（BaiZe.Mediator，镜像 MassTransit 的 in-process 用法）与门面。
/// Consumer 模型（IConsumer）与真实总线语义一致——未来要出 HTTP / 接真实 broker 时，本方法无需改动。
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        // 进程内 Mediator：无传输、消息按引用传递；注册本程序集的 Consumer（命令 + 事件订阅者）。
        services.AddMediator(cfg =>
        {
            cfg.AddConsumer<Commands.StartMeetingConsumer>();
            cfg.AddConsumer<Commands.ExecuteVoiceCommandConsumer>();
            cfg.AddConsumer<Commands.SummarizeMeetingConsumer>();
            cfg.AddConsumer<Queries.GetMeetingConsumer>();

            // 领域事件订阅者（Publish 多订阅者分发，可无人订阅）。
            cfg.AddConsumer<Events.CommandExecutedConsumer>();

            // 横切管线行为（先注册者处于最外层）。
            cfg.AddBehavior<Behaviors.LoggingBehavior>();
        });
        services.AddScoped<IBaiZeFacade, BaiZeFacade>();
        return services;
    }
}

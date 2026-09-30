using Microsoft.Extensions.DependencyInjection;

namespace BaiZe.Mediator;

/// <summary>
/// 中介者 DI 扩展。用法与 MassTransit 的 services.AddMediator(cfg => ...) 一致：
/// 调用方在 cfg 中注册 Consumer（与管线行为），本方法负责把 Consumer、行为、配置与中介者本身注入容器。
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddMediator(this IServiceCollection services, Action<IMediatorConfig> configure)
    {
        var config = new MediatorConfig();
        configure(config);

        // Consumer 与行为都注册为瞬态：每次请求从当前作用域解析，依赖随作用域生命周期。
        foreach (var consumerType in config.ConsumerTypes)
            services.AddTransient(consumerType);

        foreach (var behaviorType in config.BehaviorTypes)
            services.AddTransient(behaviorType);

        // 运行期映射表为单例（与 Consumer 注册无关，进程级不变）。
        services.AddSingleton(new MediatorOptions(config.ConsumerTypes, config.BehaviorTypes));

        // 中介者本身为作用域：持有当前作用域 IServiceProvider，保证 Consumer 与其依赖共享一个作用域。
        services.AddScoped<Mediator>();
        services.AddScoped<IScopedMediator>(sp => sp.GetRequiredService<Mediator>());
        services.AddScoped<IMediator>(sp => sp.GetRequiredService<Mediator>());

        return services;
    }
}

using BaiZe.Domain.Policies;
using BaiZe.Domain.Ports;
using BaiZe.Domain.Repositories;
using BaiZe.Domain.Services;
using BaiZe.Infrastructure.Audio;
using BaiZe.Infrastructure.FunAsr;
using BaiZe.Infrastructure.Llm;
using BaiZe.Infrastructure.Persistence;
using BaiZe.Infrastructure.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BaiZe.Infrastructure;

/// <summary>基础设施 DI：注册所有端口/仓储/工具的真实（或桩）实现。</summary>
public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        var funasr = config.GetSection(FunAsrOptions.SectionName).Get<FunAsrOptions>() ?? new FunAsrOptions();
        services.AddSingleton(funasr);
        services.AddSingleton<IAsrGateway>(sp => new FunAsrGateway(
            sp.GetRequiredService<FunAsrOptions>(), sp.GetRequiredService<ILogger<FunAsrGateway>>()));
        services.AddSingleton<IRealtimeTranscriber>(sp => new FunAsrRealtimeClient(
            sp.GetRequiredService<FunAsrOptions>(), sp.GetRequiredService<ILogger<FunAsrRealtimeClient>>()));
        services.AddSingleton<ILlmGateway, LlmGateway>();
        services.AddSingleton<IAudioCapture, PortAudioCapture>();
        services.AddSingleton<IMeetingRepository, SqliteMeetingRepository>();
        services.AddSingleton<ICommandRepository, SqliteCommandRepository>();
        services.AddSingleton<ISpeakerRegistry, SpeakerRegistry>();

        // 全自动护栏：首版默认 DryRun=true，验证 ITool 路由正确后再切 prod
        services.AddSingleton<IExecutionPolicy>(_ => new DefaultExecutionPolicy(dryRunByDefault: true));
        services.AddSingleton<IntentResolutionService>();
        services.AddSingleton<CommandDispatcher>();
        services.AddSingleton<SpeakerLabelingService>();

        services.AddSingleton<ITool, OpenAppTool>();
        services.AddSingleton<ITool, BookFlightTool>();
        services.AddSingleton<ITool, QueryReportTool>();

        services.AddSingleton<IHostedService, FunAsrServerHost>();
        return services;
    }
}

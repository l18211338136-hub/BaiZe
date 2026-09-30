using Avalonia;
using BaiZe.Application;
using BaiZe.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BaiZe.Desktop;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // 首版：前端进程内直调后端核心（Application + Infrastructure 同进程装配），不出 HTTP。
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        // 日志输出到随 exe 打开的控制台窗口（双击运行时可见），级别由 appsettings Logging 节控制
        services.AddLogging(builder =>
        {
            builder.AddConfiguration(configuration.GetSection("Logging"));
            builder.AddSimpleConsole(o =>
            {
                o.SingleLine = true;
                o.TimestampFormat = "HH:mm:ss ";
            });
        });
        services.AddApplication();
        services.AddInfrastructure(configuration);
        App.Services = services.BuildServiceProvider();

        // 拉起 IHostedService（FunASrServerHost：自动装依赖 + 托管本地 funasr-server 进程）
        var hostedServices = StartHostedServices(App.Services);

        // 阻塞运行桌面主循环；窗口关闭后回收 funasr-server 等后台进程
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

        StopHostedServices(hostedServices);
    }

    private static List<Microsoft.Extensions.Hosting.IHostedService> StartHostedServices(IServiceProvider services)
    {
        var started = new List<Microsoft.Extensions.Hosting.IHostedService>();
        foreach (var hosted in services.GetServices<Microsoft.Extensions.Hosting.IHostedService>())
        {
            _ = hosted.StartAsync(CancellationToken.None);
            started.Add(hosted);
        }
        return started;
    }

    private static void StopHostedServices(List<Microsoft.Extensions.Hosting.IHostedService> hostedServices)
    {
        foreach (var hosted in hostedServices)
        {
            try { hosted.StopAsync(CancellationToken.None).GetAwaiter().GetResult(); }
            catch { /* 退出阶段尽力回收，不阻断 */ }
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}

using Avalonia.Controls;
using BaiZe.Application;
using BaiZe.Desktop.ViewModels;
using BaiZe.Domain.Ports;
using Microsoft.Extensions.DependencyInjection;

namespace BaiZe.Desktop;

public partial class MainWindow : Window
{
    /// <summary>true = 允许真正关闭（托盘"退出"）；false = 关窗只是隐藏到托盘。</summary>
    public bool AllowRealClose { get; set; }

    public MainWindow()
    {
        DataContext = new ShellViewModel(
            App.Services.GetRequiredService<IBaiZeFacade>(),
            App.Services.GetRequiredService<IRealtimeTranscriber>(),
            App.Services.GetRequiredService<IAudioCapture>());
        InitializeComponent();
    }

    /// <summary>点关闭按钮 = 隐藏到托盘（会议转写等后台服务继续运行），从托盘菜单退出才真正关闭。</summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!AllowRealClose)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Microsoft.Extensions.DependencyInjection;

namespace BaiZe.Desktop;

public class App : Avalonia.Application
{
    /// <summary>进程内 DI 容器（首版前端与后端同进程，直调门面，不出 HTTP）。</summary>
    public static IServiceProvider Services { get; set; } = null!;

    private TrayIcon? _trayIcon;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
            SetupTrayIcon();
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>系统托盘：代码构建（XAML 声明 TrayIcon 在 XamlIlPopulate 时 NRE）。
    /// 关闭窗口 = 隐藏到托盘；左键图标/菜单"打开主窗口"唤回；菜单"退出"真正关闭。</summary>
    private void SetupTrayIcon()
    {
        var open = new NativeMenuItem("打开主窗口");
        open.Click += (_, _) => ShowMainWindow();

        var exit = new NativeMenuItem("退出");
        exit.Click += (_, _) =>
        {
            _trayIcon?.Dispose();   // 先摘托盘图标再关，避免退出时残留
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                if (desktop.MainWindow is MainWindow w) w.AllowRealClose = true;
                desktop.Shutdown();
            }
        };

        var menu = new NativeMenu();
        menu.Items.Add(open);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(exit);

        _trayIcon = new TrayIcon
        {
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://BaiZe.Desktop/Assets/app-icon.ico"))),
            ToolTipText = "白泽 BaiZe — 语音工作助手",
            Menu = menu,
            IsVisible = true
        };
        // 左键单击图标 → 唤回主窗口
        _trayIcon.Clicked += (_, _) => ShowMainWindow();

        TrayIcon.SetIcons(this, new TrayIcons { _trayIcon });
    }

    private static MainWindow? MainWindow =>
        Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime
            is { } desktop ? desktop.MainWindow as MainWindow : null;

    private static void ShowMainWindow()
    {
        if (MainWindow is not { } w) return;
        w.Show();
        w.WindowState = WindowState.Normal;
        w.Activate();
    }
}

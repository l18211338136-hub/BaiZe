using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using BaiZe.Desktop.ViewModels;

namespace BaiZe.Desktop.Views;

public partial class ChatView : UserControl
{
    private ChatViewModel? _vm;

    public ChatView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null)
                _vm.Messages.CollectionChanged -= OnMessagesChanged;
            _vm = DataContext as ChatViewModel;
            if (_vm is not null)
                _vm.Messages.CollectionChanged += OnMessagesChanged;
        };
    }

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // 双保险：万一消息在后台线程被 Add（如语音流中途入队），先回到 UI 线程再滚到底，
        // 否则 ScrollViewer 会抛 "Call from invalid thread"。
        if (Dispatcher.UIThread.CheckAccess())
            Scroll.ScrollToEnd();
        else
            Dispatcher.UIThread.Post(() => Scroll.ScrollToEnd());
    }

    /// <summary>输入框聚焦时给外层卡片打上光圈态（青绿描边 + 深投影），失焦回落。</summary>
    private void OnInputFocusChanged(object? sender, RoutedEventArgs e)
    {
        if (InputBox.IsFocused)
            InputCard.Classes.Add("inputFocused");
        else
            InputCard.Classes.Remove("inputFocused");
    }
}

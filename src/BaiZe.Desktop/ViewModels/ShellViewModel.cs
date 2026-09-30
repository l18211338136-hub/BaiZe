using BaiZe.Application;
using BaiZe.Domain.Ports;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BaiZe.Desktop.ViewModels;

/// <summary>
/// 应用外壳：左侧导航（对话 / 会议），右侧内容区在两个页面间切换。
/// </summary>
public partial class ShellViewModel : ObservableObject
{
    public ChatViewModel Chat { get; }
    public MeetingViewModel Meeting { get; }

    [ObservableProperty] private ObservableObject _currentPage;

    public ShellViewModel(IBaiZeFacade facade, IRealtimeTranscriber transcriber, IAudioCapture capture)
    {
        // 语音输入/会议录制共用单例的转写客户端与采集器，两边靠各自 IsListening/IsRecording 互斥
        Chat = new ChatViewModel(facade, transcriber, capture);
        Meeting = new MeetingViewModel(facade, transcriber, capture);
        _currentPage = Chat;
    }

    public bool IsChatActive => ReferenceEquals(CurrentPage, Chat);
    public bool IsMeetingActive => ReferenceEquals(CurrentPage, Meeting);

    partial void OnCurrentPageChanged(ObservableObject value)
    {
        OnPropertyChanged(nameof(IsChatActive));
        OnPropertyChanged(nameof(IsMeetingActive));
    }

    [RelayCommand]
    private void ShowChat() => CurrentPage = Chat;

    [RelayCommand]
    private void ShowMeeting() => CurrentPage = Meeting;
}

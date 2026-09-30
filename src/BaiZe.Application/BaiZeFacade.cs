using BaiZe.Contracts;
using BaiZe.Mediator;

namespace BaiZe.Application;

/// <summary>
/// 应用门面：表现层（Avalonia / 未来 HTTP 适配器）唯一入口。
/// 进程内直调时由 Desktop 组合根解析；出接口时由 Backend.Http 套 Controller 调用，业务层零改动。
/// 请求/响应走自研进程内 Mediator（BaiZe.Mediator）的 CreateRequestClient + GetResponse。
/// </summary>
public interface IBaiZeFacade
{
    Task<MeetingDto> StartMeetingAsync(string title, CancellationToken ct = default);
    Task<CommandResultDto> ExecuteVoiceCommandAsync(string rawText, CancellationToken ct = default);
    Task<MeetingDto> SummarizeMeetingAsync(Guid meetingId, CancellationToken ct = default);
    Task<MeetingDto> GetMeetingAsync(Guid meetingId, CancellationToken ct = default);
}

public sealed class BaiZeFacade : IBaiZeFacade
{
    private readonly IScopedMediator _mediator;
    public BaiZeFacade(IScopedMediator mediator) => _mediator = mediator;

    public async Task<MeetingDto> StartMeetingAsync(string title, CancellationToken ct = default)
    {
        var client = _mediator.CreateRequestClient<Commands.StartMeetingCommand>();
        var response = await client.GetResponse<MeetingDto>(new Commands.StartMeetingCommand(title), ct);
        return response.Message;
    }

    public async Task<CommandResultDto> ExecuteVoiceCommandAsync(string rawText, CancellationToken ct = default)
    {
        var client = _mediator.CreateRequestClient<Commands.ExecuteVoiceCommandCommand>();
        var response = await client.GetResponse<CommandResultDto>(new Commands.ExecuteVoiceCommandCommand(rawText), ct);
        return response.Message;
    }

    public async Task<MeetingDto> SummarizeMeetingAsync(Guid meetingId, CancellationToken ct = default)
    {
        var client = _mediator.CreateRequestClient<Commands.SummarizeMeetingCommand>();
        var response = await client.GetResponse<MeetingDto>(new Commands.SummarizeMeetingCommand(meetingId), ct);
        return response.Message;
    }

    public async Task<MeetingDto> GetMeetingAsync(Guid meetingId, CancellationToken ct = default)
    {
        var client = _mediator.CreateRequestClient<Queries.GetMeetingQuery>();
        var response = await client.GetResponse<MeetingDto>(new Queries.GetMeetingQuery(meetingId), ct);
        return response.Message;
    }
}

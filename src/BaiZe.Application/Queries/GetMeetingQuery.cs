using BaiZe.Contracts;
using BaiZe.Domain.Meetings;
using BaiZe.Domain.Repositories;
using BaiZe.Mediator;

namespace BaiZe.Application.Queries;

/// <summary>查询会议详情（含字幕与说话人）。</summary>
public sealed record GetMeetingQuery(Guid MeetingId);

public sealed class GetMeetingConsumer : IConsumer<GetMeetingQuery>
{
    private readonly IMeetingRepository _repo;
    public GetMeetingConsumer(IMeetingRepository repo) => _repo = repo;

    public async Task Consume(ConsumeContext<GetMeetingQuery> context)
    {
        var req = context.Message;
        var m = await _repo.GetAsync(req.MeetingId, context.CancellationToken)
                ?? throw new InvalidOperationException($"Meeting {req.MeetingId} not found.");
        await context.RespondAsync(BaiZe.Application.Commands.StartMeetingConsumer.Map(m));
    }
}

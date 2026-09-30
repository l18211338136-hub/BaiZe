using BaiZe.Contracts;
using BaiZe.Domain.Meetings;
using BaiZe.Domain.Repositories;
using BaiZe.Mediator;

namespace BaiZe.Application.Commands;

/// <summary>开始会议命令（CQRS，BaiZe.Mediator 请求消息）。</summary>
public sealed record StartMeetingCommand(string Title);

public sealed class StartMeetingConsumer : IConsumer<StartMeetingCommand>
{
    private readonly IMeetingRepository _repo;
    public StartMeetingConsumer(IMeetingRepository repo) => _repo = repo;

    public async Task Consume(ConsumeContext<StartMeetingCommand> context)
    {
        var req = context.Message;
        var meeting = new Meeting(Guid.NewGuid(), req.Title);
        meeting.Start();
        await _repo.SaveAsync(meeting, context.CancellationToken);
        await context.RespondAsync(Map(meeting));
    }

    internal static MeetingDto Map(Meeting m) =>
        new(m.Id, m.Title, m.CreatedAt, (Contracts.MeetingStatus)(int)m.Status,
            m.Segments.Select(s => new TranscriptSegmentDto(s.SpeakerId.Value.ToString(), s.Text, s.StartMs, s.EndMs)).ToList(),
            m.Speakers.Select(s => new SpeakerInfo(s.Id.Value.ToString(), s.User?.DisplayName, s.User?.AvatarUrl)).ToList(),
            m.Summary);
}

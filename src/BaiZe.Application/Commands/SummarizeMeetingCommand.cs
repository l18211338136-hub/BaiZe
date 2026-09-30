using BaiZe.Contracts;
using BaiZe.Domain.Meetings;
using BaiZe.Domain.Ports;
using BaiZe.Domain.Repositories;
using BaiZe.Mediator;

namespace BaiZe.Application.Commands;

/// <summary>生成会议纪要命令（调用 LLM 总结）。</summary>
public sealed record SummarizeMeetingCommand(Guid MeetingId);

public sealed class SummarizeMeetingConsumer : IConsumer<SummarizeMeetingCommand>
{
    private readonly IMeetingRepository _meetingRepo;
    private readonly ILlmGateway _llm;

    public SummarizeMeetingConsumer(IMeetingRepository meetingRepo, ILlmGateway llm)
    {
        _meetingRepo = meetingRepo;
        _llm = llm;
    }

    public async Task Consume(ConsumeContext<SummarizeMeetingCommand> context)
    {
        var req = context.Message;
        var m = await _meetingRepo.GetAsync(req.MeetingId, context.CancellationToken)
                ?? throw new InvalidOperationException($"Meeting {req.MeetingId} not found.");
        var transcript = string.Join("\n", m.Segments.Select(s => s.Text));
        var summary = await _llm.SummarizeAsync(transcript, context.CancellationToken);
        m.SetSummary(summary);
        m.Complete();
        await _meetingRepo.SaveAsync(m, context.CancellationToken);
        await context.RespondAsync(StartMeetingConsumer.Map(m));
    }
}

using BaiZe.Domain.Commands;

namespace BaiZe.Domain.Repositories;

/// <summary>指令仓储端口，持久化语音指令与执行结果（审计用）。</summary>
public interface ICommandRepository
{
    Task SaveAsync(VoiceCommand command, CancellationToken ct = default);
    Task<IReadOnlyList<VoiceCommand>> ListRecentAsync(int count, CancellationToken ct = default);
}

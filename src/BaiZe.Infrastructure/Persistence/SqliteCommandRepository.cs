using BaiZe.Domain.Commands;
using BaiZe.Domain.Repositories;

namespace BaiZe.Infrastructure.Persistence;

/// <summary>指令仓储（桩）。首版内存；后续落 SQLite，供审计回放。</summary>
public sealed class SqliteCommandRepository : ICommandRepository
{
    private readonly List<VoiceCommand> _store = new();

    public Task SaveAsync(VoiceCommand command, CancellationToken ct = default)
    {
        _store.Add(command);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<VoiceCommand>> ListRecentAsync(int count, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<VoiceCommand>>(_store.TakeLast(count).ToList());
}

using BaiZe.Domain.Meetings;
using BaiZe.Domain.Repositories;

namespace BaiZe.Infrastructure.Persistence;

/// <summary>SQLite 会议仓储（桩）。首版内存字典；后续用 EF Core / Dapper 落地到本地 SQLite。</summary>
public sealed class SqliteMeetingRepository : IMeetingRepository
{
    private readonly Dictionary<Guid, Meeting> _store = new();

    public Task<Meeting?> GetAsync(Guid id, CancellationToken ct = default) =>
        Task.FromResult(_store.TryGetValue(id, out var m) ? m : null);

    public Task SaveAsync(Meeting meeting, CancellationToken ct = default)
    {
        _store[meeting.Id] = meeting;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Meeting>> ListRecentAsync(int count, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Meeting>>(_store.Values.Take(count).ToList());
}

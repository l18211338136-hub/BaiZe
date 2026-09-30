using BaiZe.Domain.Meetings;

namespace BaiZe.Domain.Repositories;

/// <summary>会议仓储端口（六边形接口），由 Infrastructure 的 SQLite 实现。</summary>
public interface IMeetingRepository
{
    Task<Meeting?> GetAsync(Guid id, CancellationToken ct = default);
    Task SaveAsync(Meeting meeting, CancellationToken ct = default);
    Task<IReadOnlyList<Meeting>> ListRecentAsync(int count, CancellationToken ct = default);
}

using IntegrationHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace IntegrationHub.Infrastructure.Persistence.Repositories;

public sealed class UserActivityRepository : IUserActivityRepository
{
    private readonly UserActivityDbContext _db;

    public UserActivityRepository(UserActivityDbContext db)
    {
        _db = db;
    }

    public async Task<List<UserActivity>> GetHistoryByUserIdAsync(int userId, int limit, CancellationToken ct) =>
        await _db.UserActivities
            .AsNoTracking()
            .Where(activity => activity.UserId == userId)
            .OrderByDescending(activity => activity.Timestamp)
            .Take(limit)
            .ToListAsync(ct);

    public Task DeleteHistoryByUserIdAsync(int userId, CancellationToken ct) =>
        _db.UserActivities
            .Where(activity => activity.UserId == userId)
            .ExecuteDeleteAsync(ct);

    public Task<UserActivity?> FindDuplicateActivityAsync(
        int userId,
        string serviceType,
        string subCategory,
        string? inputValue,
        string? resultValue,
        CancellationToken ct) =>
        _db.UserActivities.FirstOrDefaultAsync(
            activity => activity.UserId == userId &&
                        activity.ServiceType == serviceType &&
                        activity.SubCategory == subCategory &&
                        activity.InputValue == inputValue &&
                        activity.ResultValue == resultValue,
            ct);

    public async Task AddActivityAsync(UserActivity activity, CancellationToken ct)
    {
        _db.UserActivities.Add(activity);
        await _db.SaveChangesAsync(ct);
    }

    public Task SaveChangesAsync(CancellationToken ct) => _db.SaveChangesAsync(ct);
}

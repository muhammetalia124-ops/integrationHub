using Microsoft.Extensions.Logging;

namespace IntegrationHub.Domain;

public sealed class UserActivityService
{
    private const int HistoryLimit = 200;

    private readonly IUserActivityRepository _activities;
    private readonly ILogger<UserActivityService> _logger;

    public UserActivityService(IUserActivityRepository activities, ILogger<UserActivityService> logger)
    {
        _activities = activities;
        _logger = logger;
    }

    public async Task<List<UserActivityDto>> GetHistoryAsync(int userId, CancellationToken ct) =>
        await GetHistoryEntitiesAsync(userId, ct);

    public Task<List<UserActivityDto>> GetHistoryRangeAsync(int userId, CancellationToken ct) =>
        GetHistoryEntitiesAsync(userId, ct);

    public Task ClearHistoryAsync(int userId, CancellationToken ct) =>
        _activities.DeleteHistoryByUserIdAsync(userId, ct);

    public async Task<bool> SaveIdempotentAsync(int userId, CalculateResponse result, CancellationToken ct)
    {
        try
        {
            var duplicate = await _activities.FindDuplicateActivityAsync(
                userId,
                result.ServiceType,
                result.SubCategory,
                result.InputValue,
                result.ResultValue,
                ct);

            if (duplicate is not null)
            {
                duplicate.Timestamp = DateTime.UtcNow;
                await _activities.SaveChangesAsync(ct);
                return false;
            }

            await _activities.AddActivityAsync(new UserActivity
            {
                UserId = userId,
                ServiceType = result.ServiceType,
                SubCategory = result.SubCategory,
                InputLabel = result.InputLabel,
                InputValue = result.InputValue,
                ResultLabel = result.ResultLabel,
                ResultValue = result.ResultValue,
                Timestamp = DateTime.UtcNow
            }, ct);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Aktivite kaydı saklanamadı (userId={UserId})", userId);
            return false;
        }
    }

    private async Task<List<UserActivityDto>> GetHistoryEntitiesAsync(int userId, CancellationToken ct)
    {
        var activities = await _activities.GetHistoryByUserIdAsync(userId, HistoryLimit, ct);

        return activities.Select(ToDto).ToList();
    }

    internal static UserActivityDto ToDto(UserActivity activity) => new()
    {
        Id = activity.Id,
        ServiceType = activity.ServiceType,
        SubCategory = activity.SubCategory,
        InputLabel = activity.InputLabel,
        InputValue = activity.InputValue,
        ResultLabel = activity.ResultLabel,
        ResultValue = activity.ResultValue,
        Timestamp = activity.Timestamp
    };
}

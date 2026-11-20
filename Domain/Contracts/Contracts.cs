namespace IntegrationHub.Domain;

public interface IPasswordHasher
{
    string Hash(string password);
    bool Verify(string password, string passwordHash);
}

public interface ITokenIssuer
{
    string CreateToken(User user, DateTime expiresAt);
}

public interface IUserRepository
{
    Task<User?> GetByIdAsync(int id, CancellationToken ct);
    Task<User?> GetByEmailAsync(string email, CancellationToken ct);
    Task<bool> AnyByEmailAsync(string email, CancellationToken ct);
    Task<bool> AnyByEmailExceptIdAsync(string email, int exceptId, CancellationToken ct);
    Task<List<User>> GetAllOrderedAsync(CancellationToken ct);
    Task<int> CountActiveAdminsAsync(CancellationToken ct);
    Task<bool> AnyAdminAsync(CancellationToken ct);
    Task<bool> IsActiveAdminAsync(int id, CancellationToken ct);
    Task AddAsync(User user, CancellationToken ct);
    Task UpdateAsync(User user, CancellationToken ct);
    Task DeleteAsync(User user, CancellationToken ct);
    Task SaveChangesAsync(CancellationToken ct);
}

public interface IUserActivityRepository
{
    Task<List<UserActivity>> GetHistoryByUserIdAsync(int userId, int limit, CancellationToken ct);
    Task DeleteHistoryByUserIdAsync(int userId, CancellationToken ct);
    Task<UserActivity?> FindDuplicateActivityAsync(
        int userId,
        string serviceType,
        string subCategory,
        string? inputValue,
        string? resultValue,
        CancellationToken ct);
    Task AddActivityAsync(UserActivity activity, CancellationToken ct);
    Task SaveChangesAsync(CancellationToken ct);
}

public interface IIntegrationService
{
    string ServiceType { get; }
    string DisplayName { get; }
    string Icon { get; }
    bool IsEnabled { get; }

    CategoryMetadata BuildMetadata();
    Task<CalculateResponse> CalculateAsync(CalculateRequest request, int userId, CancellationToken ct = default);
}

public interface ILocationLookupService
{
    Task<ResolvedLocationResponse?> ResolveAsync(double latitude, double longitude, CancellationToken ct);
}

public interface IExternalApiCache
{
    Task<T?> GetOrCreateAsync<T>(
        string cacheKey,
        TimeSpan lifetime,
        Func<CancellationToken, Task<T?>> factory,
        Func<T, bool> shouldCache,
        CancellationToken ct)
        where T : class;
}

public interface IExchangeRateProvider
{
    Task<IReadOnlyDictionary<string, CurrencyRate>> GetRatesAsync(CancellationToken ct);
}

public interface ISharedApiDataStore
{
    Task<List<SharedApiDataRecord>> GetSharedDataAsync(
        string? dataType,
        DateOnly? from,
        DateOnly? to,
        int take,
        CancellationToken ct);
    Task<SharedApiDataRecord?> GetSharedDataAsync(int id, CancellationToken ct);
    Task<SharedApiDataRecord?> GetAdminManagedDailyAsync(
        string dataType,
        string recordKey,
        DateOnly dataDate,
        CancellationToken ct);
    Task<bool> SharedDataExistsAsync(
        string dataType,
        string recordKey,
        DateOnly dataDate,
        int exceptId,
        CancellationToken ct);
    Task SaveSharedDataAsync(SharedApiDataRecord record, CancellationToken ct);
    Task UpsertDailyRangeAsync(IReadOnlyCollection<SharedApiDataRecord> records, CancellationToken ct);
}

public interface ICurrencyCacheStore
{
    Task<List<CurrencyRate>> GetRatesAsync(CancellationToken ct);

    Task<IReadOnlyList<CurrencyRate>> ReplaceRatesAsync(IReadOnlyCollection<CurrencyRate> rates, CancellationToken ct);
}

public interface IPrayerTimeCacheStore
{
    Task<PrayerTimeCache?> GetPrayerTimesAsync(string districtId, DateOnly prayerDate, CancellationToken ct);
    Task UpsertPrayerTimesAsync(IReadOnlyCollection<PrayerTimeCache> entries, CancellationToken ct);
}

public interface IDatabaseInitializer
{
    Task InitializeAsync(CancellationToken ct = default);
}

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using IntegrationHub.Domain;

namespace IntegrationHub.Infrastructure.Persistence;

public sealed class DatabaseInitializer : IDatabaseInitializer
{
    private const string AdminEmailKey = "AdminBootstrap:Email";
    private const string AdminUsernameKey = "AdminBootstrap:Username";
    private const string AdminPasswordKey = "AdminBootstrap:Password";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IUserRepository _users;
    private readonly UserActivityDbContext _activities;
    private readonly CurrencyRateDbContext _cache;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IConfiguration _configuration;
    private readonly ILogger<DatabaseInitializer> _logger;

    public DatabaseInitializer(
        IServiceScopeFactory scopeFactory,
        IUserRepository users,
        UserActivityDbContext activities,
        CurrencyRateDbContext cache,
        IPasswordHasher passwordHasher,
        IConfiguration configuration,
        ILogger<DatabaseInitializer> logger)
    {
        _scopeFactory = scopeFactory;
        _users = users;
        _activities = activities;
        _cache = cache;
        _passwordHasher = passwordHasher;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await MigrationState.MigrateAsync(_scopeFactory, typeof(UserDbContext), "Auth", "Users", ct);
        await MigrationState.MigrateAsync(_scopeFactory, typeof(CurrencyRateDbContext), "Cache", "SharedApiData", ct);
        await ArchiveExistingRatesAsync(ct);
        await MigrationState.MigrateAsync(_scopeFactory, typeof(UserActivityDbContext), "Activity", "UserActivities", ct);
        await SeedAdminAsync(ct);
    }

    private async Task ArchiveExistingRatesAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var cacheStore = scope.ServiceProvider.GetRequiredService<ICurrencyCacheStore>();
        var sharedData = scope.ServiceProvider.GetRequiredService<ISharedApiDataStore>();

        var rates = await cacheStore.GetRatesAsync(ct);
        if (rates.Count == 0)
        {
            return;
        }

        await sharedData.UpsertDailyRangeAsync(
            rates.Select(rate => new SharedApiDataRecord
            {
                DataType = SharedApiDataTypes.Currency,
                RecordKey = rate.Code,
                DisplayName = rate.Name,
                DataDate = DateOnly.FromDateTime(rate.FetchedAtUtc),
                PayloadJson = JsonSerializer.Serialize(rate, JsonOptions),
                FetchedAtUtc = rate.FetchedAtUtc
            }).ToList(),
            ct);
    }

    private async Task SeedAdminAsync(CancellationToken ct)
    {
        if (await _users.AnyAdminAsync(ct))
        {
            return;
        }

        var email = _configuration[AdminEmailKey]?.Trim();
        var username = _configuration[AdminUsernameKey]?.Trim();
        var password = _configuration[AdminPasswordKey];
        if (string.IsNullOrWhiteSpace(email) ||
            string.IsNullOrWhiteSpace(username) ||
            string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException(
                "İlk yönetici hesabı için AdminBootstrap:Email / Username / Password ayarları eksik.");
        }

        var user = await _users.GetByEmailAsync(email, ct);
        if (user is null)
        {
            user = new User
            {
                Email = email,
                Username = username,
                PasswordHash = _passwordHasher.Hash(password),
                CreatedAt = DateTime.UtcNow
            };
        }
        else
        {
            user.Username = username;
        }

        user.IsAdmin = true;
        user.IsActive = true;
        await _users.UpdateAsync(user, ct);
        _logger.LogInformation("İlk yönetici hesabı hazırlandı: {Email}", user.Email);
    }
}

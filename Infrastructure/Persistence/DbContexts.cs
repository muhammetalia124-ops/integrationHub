using IntegrationHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace IntegrationHub.Infrastructure.Persistence;

public class UserDbContext : DbContext
{
    public UserDbContext(DbContextOptions<UserDbContext> options) : base(options) { }

    public DbSet<User> Users => Set<User>();
    public DbSet<AuthVerificationCode> AuthVerificationCodes => Set<AuthVerificationCode>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new UserConfiguration());
        modelBuilder.ApplyConfiguration(new AuthVerificationCodeConfiguration());
        modelBuilder.ApplyConfiguration(new RefreshTokenConfiguration());
    }
}

public class UserActivityDbContext : DbContext
{
    public UserActivityDbContext(DbContextOptions<UserActivityDbContext> options) : base(options) { }

    public DbSet<UserActivity> UserActivities => Set<UserActivity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfiguration(new UserActivityConfiguration());
}

public sealed class CurrencyRateDbContext : DbContext
{
    public CurrencyRateDbContext(DbContextOptions<CurrencyRateDbContext> options) : base(options) { }

    public DbSet<CurrencyRate> CurrencyRates => Set<CurrencyRate>();
    public DbSet<PrayerTimeCache> PrayerTimeCache => Set<PrayerTimeCache>();
    public DbSet<ExternalApiCacheEntry> ExternalApiCache => Set<ExternalApiCacheEntry>();
    public DbSet<SharedApiDataRecord> SharedApiData => Set<SharedApiDataRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new CurrencyRateConfiguration());
        modelBuilder.ApplyConfiguration(new PrayerTimeCacheConfiguration());
        modelBuilder.ApplyConfiguration(new ExternalApiCacheConfiguration());
        modelBuilder.ApplyConfiguration(new SharedApiDataConfiguration());
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace IntegrationHub.Infrastructure.Persistence;

public sealed class UserDbContextFactory : IDesignTimeDbContextFactory<UserDbContext>
{
    public UserDbContext CreateDbContext(string[] args) =>
        new(PostgresContextOptions.Create<UserDbContext>("UserDbConnection"));
}

public sealed class UserActivityDbContextFactory : IDesignTimeDbContextFactory<UserActivityDbContext>
{
    public UserActivityDbContext CreateDbContext(string[] args) =>
        new(PostgresContextOptions.Create<UserActivityDbContext>("ActivityDbConnection"));
}

public sealed class CurrencyRateDbContextFactory : IDesignTimeDbContextFactory<CurrencyRateDbContext>
{
    public CurrencyRateDbContext CreateDbContext(string[] args) =>
        new(PostgresContextOptions.Create<CurrencyRateDbContext>("CurrencyRatesConnection"));
}

internal static class PostgresContextOptions
{
    public static DbContextOptions<TContext> Create<TContext>(string connectionName)
        where TContext : DbContext
    {
        var connectionString = Environment.GetEnvironmentVariable($"ConnectionStrings__{connectionName}");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"Migration için ConnectionStrings__{connectionName} ortam değişkenini ayarlayın.");
        }

        return new DbContextOptionsBuilder<TContext>()
            .UseNpgsql(connectionString)
            .Options;
    }
}

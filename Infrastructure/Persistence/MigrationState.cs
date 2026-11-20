using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace IntegrationHub.Infrastructure.Persistence;

internal static class MigrationState
{
    private const string HistoryTable = "__EFMigrationsHistory";

    public static async Task MigrateAsync(
        IServiceScopeFactory scopeFactory,
        Type contextType,
        string schema,
        string markerTable,
        CancellationToken ct)
    {
        if (await TableExistsAsync(scopeFactory, contextType, schema, markerTable, ct))
        {
            await MarkAppliedAsync(scopeFactory, contextType, ct);
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService(contextType).AsDbContext().Database.MigrateAsync(ct);
    }

    private static async Task<bool> TableExistsAsync(
        IServiceScopeFactory scopeFactory,
        Type contextType,
        string schema,
        string table,
        CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService(contextType).AsDbContext();

        var result = await db.Database
            .SqlQuery<int>($"SELECT COUNT(*)::int AS \"Value\" FROM information_schema.tables WHERE table_schema = {schema} AND table_name = {table}")
            .FirstAsync(ct);
        return result > 0;
    }

    private static async Task MarkAppliedAsync(IServiceScopeFactory scopeFactory, Type contextType, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService(contextType).AsDbContext();

        var migrations = db.GetService<IMigrationsAssembly>().Migrations.Keys.ToList();
        if (migrations.Count == 0)
        {
            return;
        }

        await db.Database.ExecuteSqlAsync(
            $"CREATE TABLE IF NOT EXISTS \"{HistoryTable}\" (\"MigrationId\" character varying(150) NOT NULL, \"ProductVersion\" character varying(32) NOT NULL, CONSTRAINT \"PK_{HistoryTable}\" PRIMARY KEY (\"MigrationId\"))",
            ct);

        foreach (var migrationId in migrations)
        {
            await db.Database.ExecuteSqlAsync(
                $"INSERT INTO \"{HistoryTable}\" (\"MigrationId\", \"ProductVersion\") VALUES ({migrationId}, '8.0.11') ON CONFLICT (\"MigrationId\") DO NOTHING",
                ct);
        }
    }

    private static DbContext AsDbContext(this object context) => (DbContext)context;
}

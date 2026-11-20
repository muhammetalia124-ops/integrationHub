using System.Collections.Concurrent;
using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using IntegrationHub.Domain;

namespace IntegrationHub.Infrastructure.Persistence;

public sealed class SharedCacheStore : IExternalApiCache, ISharedApiDataStore, ICurrencyCacheStore, IPrayerTimeCacheStore
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> RefreshLocks = new(StringComparer.Ordinal);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly CurrencyRateDbContext _db;
    private readonly ILogger<SharedCacheStore> _logger;

    public SharedCacheStore(CurrencyRateDbContext db, ILogger<SharedCacheStore> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<T?> GetOrCreateAsync<T>(
        string cacheKey,
        TimeSpan lifetime,
        Func<CancellationToken, Task<T?>> factory,
        Func<T, bool> shouldCache,
        CancellationToken ct)
        where T : class
    {
        var cached = await GetValidEntryAsync(cacheKey, ct);
        if (cached is not null)
        {
            return Deserialize<T>(cached, cacheKey);
        }

        var refreshLock = RefreshLocks.GetOrAdd(cacheKey, _ => new SemaphoreSlim(1, 1));
        await refreshLock.WaitAsync(ct);
        try
        {
            cached = await GetValidEntryAsync(cacheKey, ct);
            if (cached is not null)
            {
                return Deserialize<T>(cached, cacheKey);
            }

            var value = await factory(ct);
            if (value is null || !shouldCache(value))
            {
                return value;
            }

            var nowUtc = DateTime.UtcNow;
            await _db.ExternalApiCache
                .Where(entry => entry.CacheKey == cacheKey || entry.ExpiresAtUtc <= nowUtc)
                .ExecuteDeleteAsync(ct);
            _db.ExternalApiCache.Add(new ExternalApiCacheEntry
            {
                CacheKey = cacheKey,
                PayloadJson = JsonSerializer.Serialize(value, JsonOptions),
                FetchedAtUtc = nowUtc,
                ExpiresAtUtc = nowUtc.Add(lifetime)
            });
            await _db.SaveChangesAsync(ct);
            return value;
        }
        finally
        {
            refreshLock.Release();
        }
    }

    private Task<ExternalApiCacheEntry?> GetValidEntryAsync(string cacheKey, CancellationToken ct)
    {
        var nowUtc = DateTime.UtcNow;
        return _db.ExternalApiCache
            .AsNoTracking()
            .FirstOrDefaultAsync(entry => entry.CacheKey == cacheKey && entry.ExpiresAtUtc > nowUtc, ct);
    }

    private static T Deserialize<T>(ExternalApiCacheEntry entry, string cacheKey) =>
        JsonSerializer.Deserialize<T>(entry.PayloadJson, JsonOptions)
        ?? throw new JsonException($"Cache verisi okunamadı: {cacheKey}");

    public Task<List<SharedApiDataRecord>> GetSharedDataAsync(
        string? dataType,
        DateOnly? from,
        DateOnly? to,
        int take,
        CancellationToken ct)
    {
        var query = _db.SharedApiData.AsNoTracking().Where(record => !record.IsDeleted);
        if (!string.IsNullOrWhiteSpace(dataType))
        {
            query = query.Where(record => record.DataType == dataType);
        }

        if (from.HasValue)
        {
            query = query.Where(record => record.DataDate >= from.Value);
        }

        if (to.HasValue)
        {
            query = query.Where(record => record.DataDate <= to.Value);
        }

        return query
            .OrderByDescending(record => record.DataDate)
            .ThenByDescending(record => record.FetchedAtUtc)
            .Take(take)
            .ToListAsync(ct);
    }

    public Task<SharedApiDataRecord?> GetSharedDataAsync(int id, CancellationToken ct) =>
        _db.SharedApiData.FirstOrDefaultAsync(record => record.Id == id, ct);

    public Task<SharedApiDataRecord?> GetAdminManagedDailyAsync(
        string dataType,
        string recordKey,
        DateOnly dataDate,
        CancellationToken ct) =>
        _db.SharedApiData.AsNoTracking().FirstOrDefaultAsync(
            record => record.DataType == dataType &&
                      record.RecordKey == recordKey &&
                      record.DataDate == dataDate &&
                      !record.IsDeleted &&
                      record.IsAdminManaged,
            ct);

    public Task<bool> SharedDataExistsAsync(
        string dataType,
        string recordKey,
        DateOnly dataDate,
        int exceptId,
        CancellationToken ct) =>
        _db.SharedApiData.AnyAsync(
            record => record.Id != exceptId &&
                      record.DataType == dataType &&
                      record.RecordKey == recordKey &&
                      record.DataDate == dataDate &&
                      !record.IsDeleted,
            ct);

    public Task SaveSharedDataAsync(SharedApiDataRecord record, CancellationToken ct) =>
        UpsertDailyRangeAsync(new[] { record }, ct);

    public Task UpsertDailyRangeAsync(IReadOnlyCollection<SharedApiDataRecord> records, CancellationToken ct)
    {
        if (records.Count == 0)
        {
            return Task.CompletedTask;
        }

        return InTransactionAsync(async () =>
        {
            foreach (var record in records)
            {
                var existing = await _db.SharedApiData.FirstOrDefaultAsync(
                    item => item.DataType == record.DataType &&
                            item.RecordKey == record.RecordKey &&
                            item.DataDate == record.DataDate,
                    ct);

                if (existing is null)
                {
                    _db.SharedApiData.Add(record);
                    continue;
                }

                if (existing.IsAdminManaged && !existing.IsDeleted && !record.IsAdminManaged)
                {
                    continue;
                }

                if (existing.DisplayName == record.DisplayName &&
                    existing.PayloadJson == record.PayloadJson &&
                    existing.IsAdminManaged == record.IsAdminManaged &&
                    existing.IsDeleted == record.IsDeleted)
                {
                    continue;
                }

                existing.DisplayName = record.DisplayName;
                existing.PayloadJson = record.PayloadJson;
                existing.FetchedAtUtc = record.FetchedAtUtc;
                existing.IsAdminManaged = record.IsAdminManaged;
                existing.IsDeleted = record.IsDeleted;
            }

            if (!_db.ChangeTracker.HasChanges())
            {
                return;
            }

            await _db.SaveChangesAsync(ct);
        }, ct);
    }

    public Task<List<CurrencyRate>> GetRatesAsync(CancellationToken ct) =>
        _db.CurrencyRates.AsNoTracking().ToListAsync(ct);

    public async Task<IReadOnlyList<CurrencyRate>> ReplaceRatesAsync(
        IReadOnlyCollection<CurrencyRate> rates,
        CancellationToken ct)
    {
        var changed = new List<CurrencyRate>();

        await InTransactionAsync(async () =>
        {
            var existing = await _db.CurrencyRates.ToDictionaryAsync(rate => rate.Code, StringComparer.OrdinalIgnoreCase, ct);
            var incomingCodes = rates.Select(rate => rate.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var rate in rates)
            {
                if (!existing.TryGetValue(rate.Code, out var current))
                {
                    _db.CurrencyRates.Add(rate);
                    changed.Add(rate);
                    continue;
                }

                if (current.Name == rate.Name &&
                    current.ForexBuying == rate.ForexBuying &&
                    current.ForexSelling == rate.ForexSelling)
                {
                    continue;
                }

                current.Name = rate.Name;
                current.ForexBuying = rate.ForexBuying;
                current.ForexSelling = rate.ForexSelling;
                current.FetchedAtUtc = rate.FetchedAtUtc;
                changed.Add(current);
            }

            var removed = existing.Values.Where(rate => !incomingCodes.Contains(rate.Code)).ToList();
            if (removed.Count > 0)
            {
                _db.CurrencyRates.RemoveRange(removed);
            }

            if (changed.Count > 0 || removed.Count > 0)
            {
                await _db.SaveChangesAsync(ct);
            }
        }, ct);

        return changed;
    }

    public Task<PrayerTimeCache?> GetPrayerTimesAsync(string districtId, DateOnly prayerDate, CancellationToken ct) =>
        _db.PrayerTimeCache
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.DistrictId == districtId && item.PrayerDate == prayerDate, ct);

    public Task UpsertPrayerTimesAsync(IReadOnlyCollection<PrayerTimeCache> entries, CancellationToken ct) =>
        InTransactionAsync(async () =>
        {
            foreach (var entry in entries)
            {
                var existing = await _db.PrayerTimeCache.FirstOrDefaultAsync(
                    item => item.DistrictId == entry.DistrictId && item.PrayerDate == entry.PrayerDate,
                    ct);

                if (existing is null)
                {
                    _db.PrayerTimeCache.Add(entry);
                    continue;
                }

                existing.City = entry.City;
                existing.District = entry.District;
                existing.Imsak = entry.Imsak;
                existing.Gunes = entry.Gunes;
                existing.Ogle = entry.Ogle;
                existing.Ikindi = entry.Ikindi;
                existing.Aksam = entry.Aksam;
                existing.Yatsi = entry.Yatsi;
                existing.FetchedAtUtc = entry.FetchedAtUtc;
            }

            await _db.SaveChangesAsync(ct);
        }, ct);

    private async Task InTransactionAsync(Func<Task> action, CancellationToken ct)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        try
        {
            await action();
            await transaction.CommitAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Cache şeması işlemi geri alındı.");
            throw;
        }
    }
}

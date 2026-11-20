using IntegrationHub.Domain;
using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
namespace IntegrationHub.Integrations;

public sealed class CurrencyRateProvider : IExchangeRateProvider
{
    private const string TcmbUrl = "https://www.tcmb.gov.tr/kurlar/today.xml";

    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(5);
    private static readonly SemaphoreSlim RefreshLock = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly ISharedApiDataStore _sharedApiData;
    private readonly ICurrencyCacheStore _cacheStore;

    public CurrencyRateProvider(
        HttpClient http,
        ISharedApiDataStore sharedApiData,
        ICurrencyCacheStore cacheStore)
    {
        _http = http;
        _sharedApiData = sharedApiData;
        _cacheStore = cacheStore;
    }

    public async Task<IReadOnlyDictionary<string, CurrencyRate>> GetRatesAsync(CancellationToken ct)
    {
        var rates = await _cacheStore.GetRatesAsync(ct);
        if (IsFresh(rates))
        {
            return await WithAdminOverridesAsync(rates, ct);
        }

        await RefreshLock.WaitAsync(ct);
        try
        {
            rates = await _cacheStore.GetRatesAsync(ct);
            if (IsFresh(rates))
            {
                return await WithAdminOverridesAsync(rates, ct);
            }

            var fetchedAtUtc = DateTime.UtcNow;
            var rows = (await FetchFromTcmbAsync(ct))
                .Select(rate => new CurrencyRate
                {
                    Code = rate.Code,
                    Name = rate.Name,
                    ForexBuying = rate.ForexBuying,
                    ForexSelling = rate.ForexSelling,
                    FetchedAtUtc = fetchedAtUtc
                })
                .ToList();

            var changedRows = await _cacheStore.ReplaceRatesAsync(rows, ct);
            await ArchiveAsync(changedRows, ct);
            return await WithAdminOverridesAsync(rows, ct);
        }
        finally
        {
            RefreshLock.Release();
        }
    }

    private async Task<List<TcmbCurrency>> FetchFromTcmbAsync(CancellationToken ct)
    {
        using var response = await _http.GetAsync(TcmbUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var document = await XDocument.LoadAsync(stream, LoadOptions.None, ct);

        var rates = new List<TcmbCurrency>();
        foreach (var currency in document.Descendants("Currency"))
        {
            var code = currency.Attribute("CurrencyCode")?.Value;
            if (string.IsNullOrWhiteSpace(code))
            {
                continue;
            }

            var forexBuying = ParseDecimal(currency.Element("ForexBuying")?.Value);
            var forexSelling = ParseDecimal(currency.Element("ForexSelling")?.Value);
            if (forexBuying == 0m)
            {
                forexBuying = ParseDecimal(currency.Element("BankBuying")?.Value);
            }

            if (forexSelling == 0m)
            {
                forexSelling = ParseDecimal(currency.Element("BankSelling")?.Value);
            }

            rates.Add(new TcmbCurrency(
                code,
                currency.Element("CurrencyName")?.Value?.Trim() ?? code,
                forexBuying,
                forexSelling));
        }

        if (rates.Count == 0)
        {
            throw new InvalidOperationException("TCMB kur yanıtında kaydedilecek kur bulunamadı.");
        }

        return rates;
    }

    private static bool IsFresh(IReadOnlyCollection<CurrencyRate> rates) =>
        rates.Count > 0 && rates.Min(rate => rate.FetchedAtUtc) > DateTime.UtcNow - CacheLifetime;

    private async Task ArchiveAsync(IReadOnlyCollection<CurrencyRate> rates, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var records = rates
            .Where(rate => DateOnly.FromDateTime(rate.FetchedAtUtc) == today)
            .Select(rate => new SharedApiDataRecord
            {
                DataType = SharedApiDataTypes.Currency,
                RecordKey = rate.Code,
                DisplayName = rate.Name,
                DataDate = today,
                PayloadJson = JsonSerializer.Serialize(rate, JsonOptions),
                FetchedAtUtc = rate.FetchedAtUtc
            })
            .ToList();

        if (records.Count == 0)
        {
            return;
        }

        await _sharedApiData.UpsertDailyRangeAsync(records, ct);
    }

    private async Task<IReadOnlyDictionary<string, CurrencyRate>> WithAdminOverridesAsync(
        IEnumerable<CurrencyRate> rates,
        CancellationToken ct)
    {
        var merged = rates.ToDictionary(rate => rate.Code, StringComparer.OrdinalIgnoreCase);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var archived = await _sharedApiData.GetSharedDataAsync(SharedApiDataTypes.Currency, today, today, 500, ct);

        foreach (var record in archived.Where(item => item.IsAdminManaged))
        {
            var rate = JsonSerializer.Deserialize<CurrencyRate>(record.PayloadJson, JsonOptions);
            if (rate is null || !string.Equals(rate.Code, record.RecordKey, StringComparison.OrdinalIgnoreCase))
            {
                throw new JsonException($"Yönetici tarafından düzenlenen '{record.RecordKey}' kuru okunamadı.");
            }

            merged[rate.Code] = rate;
        }

        return merged;
    }

    private static decimal ParseDecimal(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0m;

    private sealed record TcmbCurrency(string Code, string Name, decimal ForexBuying, decimal ForexSelling);
}

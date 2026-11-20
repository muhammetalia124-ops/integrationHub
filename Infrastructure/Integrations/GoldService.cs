using IntegrationHub.Domain;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace IntegrationHub.Integrations;

public class GoldService : IIntegrationService
{
    public string ServiceType => "gold";
    public string DisplayName => "Altın";
    public string Icon => "gold";
    public bool IsEnabled => true;

    private static readonly string PricesUrl = string.Concat("https://", "finans.", "truncgil.", "com", "/v4/today.json");
    private static readonly string FallbackGoldUrl = string.Concat("https://", "api.", "gold-api.", "com", "/price/XAU");

    private static readonly TimeSpan PriceCacheLifetime = TimeSpan.FromMinutes(5);

    private static readonly decimal DefaultGram = 1m;
    private static readonly GoldProduct[] Products =
    {
        new("gram", "Gram Altın", "GRA", "gram", "TRY"),
        new("ounce", "Ons Altın", "XAUUSD", "ons", "USD"),
        new("quarter", "Çeyrek Altın", "CEYREKALTIN", "adet", "TRY"),
        new("half", "Yarım Altın", "YARIMALTIN", "adet", "TRY"),
        new("full", "Tam Altın", "TAMALTIN", "adet", "TRY"),
        new("cumhuriyet", "Cumhuriyet Altını", "CUMHURIYETALTINI", "adet", "TRY"),
        new("gremse", "Gremse Altın", "GREMSEALTIN", "adet", "TRY"),
        new("has", "Has Altın", "HAS", "gram", "TRY"),
        new("22ayar", "22 Ayar Bilezik", "YIA", "gram", "TRY"),
        new("resat", "Reşat Altın", "RESATALTIN", "adet", "TRY"),
        new("ata", "Ata Altın", "ATAALTIN", "adet", "TRY"),
        new("14ayar", "14 Ayar Altın", "14AYARALTIN", "gram", "TRY"),
        new("18ayar", "18 Ayar Altın", "18AYARALTIN", "gram", "TRY"),
        new("besli", "Beşli Altın", "BESLIALTIN", "adet", "TRY"),
        new("hamit", "Hamit Altın", "HAMITALTIN", "adet", "TRY"),
        new("ikibucuk", "İkibuçuk Altın", "IKIBUCUKALTIN", "adet", "TRY")
    };

    private readonly HttpClient _http;
    private readonly IExternalApiCache _cache;
    private readonly IExchangeRateProvider _rateProvider;
    private readonly ISharedApiDataStore _sharedApiData;
    private readonly ILogger<GoldService> _logger;
    private static readonly JsonSerializerOptions ArchiveJsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions ReadArchiveJsonOptions = new(JsonSerializerDefaults.Web);

    public GoldService(
        HttpClient http,
        IExternalApiCache cache,
        IExchangeRateProvider rateProvider,
        ISharedApiDataStore sharedApiData,
        ILogger<GoldService> logger)
    {
        _http = http;
        _cache = cache;
        _rateProvider = rateProvider;
        _sharedApiData = sharedApiData;
        _logger = logger;
    }

    public CategoryMetadata BuildMetadata()
    {
        var cat = new CategoryMetadata
        {
            ServiceType = ServiceType,
            DisplayName = DisplayName,
            Icon = Icon
        };

        foreach (var product in Products)
        {
            cat.SubCategories.Add(new SubCategoryMetadata
            {
                Key = product.Key,
                DisplayName = product.DisplayName,
                InputKind = "bidirectional",
                InputLabel = "Miktar veya nakit",
                InputPlaceholder = "Miktar veya nakit girin",
                ResultLabel = "Sonuç",
                RequiresQuantity = true,
                QuantityLabel = "Gram miktarı",
                QuantityPlaceholder = "Örn. 2.5",
                Options = new List<FieldOption>
                {
                    new() { Value = "to-tl", Label = "Miktar → TL" },
                    new() { Value = "to-count", Label = $"Nakit → {product.QuantityUnit}" }
                }
            });
        }

        return cat;
    }
    public async Task<CalculateResponse> CalculateAsync(CalculateRequest request, int userId, CancellationToken ct = default)
    {
        var product = Products.FirstOrDefault(item =>
            item.Key.Equals(request.SubCategory, StringComparison.OrdinalIgnoreCase));
        if (product is null)
        {
            return Failure("Desteklenmeyen altın tipi.");
        }

        var direction = request.Direction ?? "to-tl";

        var grams = DefaultGram;
        if (!string.IsNullOrWhiteSpace(request.Quantity))
        {
            if (!decimal.TryParse(
                    request.Quantity.Trim(),
                    NumberStyles.Number,
                    CultureInfo.InvariantCulture,
                    out grams) ||
                grams <= 0)
            {
                return Failure("Geçerli bir gram miktarı girin.");
            }
        }

        if (!decimal.TryParse(request.InputValue, NumberStyles.Any, CultureInfo.InvariantCulture, out var input) || input <= 0)
        {
            return Failure("Geçerli bir miktar girin.");
        }

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        GoldPrice providerPrice;
        string sourceLabel = "Truncgil Finans";
        try
        {
            var managedUnitPrice = await _sharedApiData.GetAdminManagedDailyAsync(
                SharedApiDataTypes.Gold, product.Key, today, ct);
            if (managedUnitPrice is not null)
            {
                var overrideValue = JsonSerializer.Deserialize<GoldPriceArchive>(
                    managedUnitPrice.PayloadJson,
                    ReadArchiveJsonOptions);
                if (overrideValue is null)
                {
                    throw new JsonException($"Yönetici altın kaydı okunamadı: {product.Key}.");
                }

                providerPrice = overrideValue.ToPrice(product);
                sourceLabel = "Yönetici Kaydı";
            }
            else
            {
                var snapshot = await GetPriceSnapshotAsync(ct);
                if (!snapshot.Prices.TryGetValue(product.Key, out providerPrice!))
                {
                    throw new JsonException($"Altın sağlayıcısı '{product.ProviderSymbol}' fiyatını döndürmedi.");
                }
                sourceLabel = snapshot.Source;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Altın fiyatı alınamadı.");
            return Failure("Altın servisine şu an ulaşılamıyor. Lütfen daha sonra tekrar deneyin.");
        }

        var price = direction == "to-tl" ? providerPrice.BuyingPrice : providerPrice.SellingPrice;
        var currency = providerPrice.Currency;
        if (currency == "USD")
        {
            var rates = await _rateProvider.GetRatesAsync(ct);
            if (!rates.TryGetValue("USD", out var usdRate))
            {
                return Failure("Ons altın için dolar kuru bulunamadı.");
            }

            var exchangeRate = direction == "to-tl" ? usdRate.ForexBuying : usdRate.ForexSelling;
            price = Math.Round(price * exchangeRate, 2, MidpointRounding.AwayFromZero);
            currency = "TRY";
        }

        if (price <= 0m)
        {
            return Failure("Altın fiyatı şu an bulunamadı.");
        }

        string resultValue;
        string resultLabel;
        string inputLabel;
        string inputValue;

        if (direction == "to-tl")
        {
            var tl = Math.Round(input * grams * price, 2, MidpointRounding.AwayFromZero);
            resultValue = tl.ToString("0.##", CultureInfo.InvariantCulture);
            resultLabel = "TL";
            inputLabel = grams == DefaultGram ? product.QuantityUnit : $"{grams.ToString("0.##", CultureInfo.InvariantCulture)} gr";
            inputValue = input.ToString("0.####", CultureInfo.InvariantCulture);
        }
        else if (direction == "to-count")
        {
            var count = decimal.Floor(input / price);
            resultValue = count.ToString("0", CultureInfo.InvariantCulture);
            resultLabel = product.QuantityUnit;
            inputLabel = "Nakit (TL)";
            inputValue = input.ToString("0.##", CultureInfo.InvariantCulture);
        }
        else
        {
            return Failure("Geçersiz hesaplama yönü.");
        }

        return new CalculateResponse
        {
            Success = true,
            ServiceType = ServiceType,
            SubCategory = product.Key,
            InputLabel = inputLabel,
            InputValue = inputValue,
            ResultLabel = resultLabel,
            ResultValue = resultValue,
            RawDetail = $"Alış {providerPrice.BuyingPrice.ToString("0.##", CultureInfo.InvariantCulture)} {providerPrice.Currency} · " +
                        $"Satış {providerPrice.SellingPrice.ToString("0.##", CultureInfo.InvariantCulture)} {providerPrice.Currency} · " +
                        $"Hesaplama {price.ToString("0.##", CultureInfo.InvariantCulture)} TL/{product.QuantityUnit}" +
                        (grams == DefaultGram ? string.Empty : $" × {grams.ToString("0.##", CultureInfo.InvariantCulture)} gr") +
                        $" · {sourceLabel}",
            Message = "Başarılı"
        };
    }

    private async Task<GoldPriceSnapshot> GetPriceSnapshotAsync(CancellationToken ct)
    {
        return await _cache.GetOrCreateAsync(
                   "gold:snapshot:v2",
                   PriceCacheLifetime,
                   FetchAndArchivePricesAsync,
                   snapshot => snapshot.Prices.Count == Products.Length,
                   ct)
               ?? throw new InvalidOperationException("Altın fiyatları şu an alınamıyor.");
    }

    private async Task<GoldPriceSnapshot?> FetchAndArchivePricesAsync(CancellationToken ct)
    {
        GoldPriceSnapshot snapshot;
        try
        {
            snapshot = await FetchPricesFromTruncgilAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Truncgil altın servisi yanıt vermedi, TCMB + Ons yedek hesaplamasına geçiliyor.");
            snapshot = await FetchPricesFromFallbackAsync(ct);
        }

        var records = Products.Select(product =>
        {
            var price = snapshot.Prices[product.Key];
            return new SharedApiDataRecord
            {
                DataType = SharedApiDataTypes.Gold,
                RecordKey = product.Key,
                DisplayName = product.DisplayName,
                DataDate = DateOnly.FromDateTime(snapshot.FetchedAtUtc),
                PayloadJson = JsonSerializer.Serialize(GoldPriceArchive.From(price), ArchiveJsonOptions),
                FetchedAtUtc = snapshot.FetchedAtUtc
            };
        }).ToList();
        await _sharedApiData.UpsertDailyRangeAsync(records, ct);
        return snapshot;
    }
        private async Task<GoldPriceSnapshot> FetchPricesFromTruncgilAsync(CancellationToken ct)
    {
        using var response = await _http.GetAsync(PricesUrl, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("GRA", out var gramQuote) ||
            gramQuote.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Truncgil altın yanıtı beklenen biçimde değil.");
        }

        var prices = new Dictionary<string, GoldPrice>(StringComparer.OrdinalIgnoreCase);
        foreach (var product in Products)
        {
            if (product.Key == "ounce")
            {
                if (!TryReadPrice(gramQuote, "Buying", out var gramBuying) ||
                    !TryReadPrice(gramQuote, "Selling", out var gramSelling) ||
                    gramBuying <= 0m || gramSelling <= 0m)
                {
                    throw new JsonException("Truncgil gram altın fiyatını döndürmedi.");
                }

                prices.Add(product.Key, new GoldPrice(
                    Math.Round(gramBuying * 31.1034768m, 4, MidpointRounding.AwayFromZero),
                    Math.Round(gramSelling * 31.1034768m, 4, MidpointRounding.AwayFromZero),
                    product.Currency,
                    product.ProviderSymbol));
                continue;
            }

            if (!root.TryGetProperty(product.ProviderSymbol, out var quote) ||
                quote.ValueKind != JsonValueKind.Object ||
                !TryReadPrice(quote, "Buying", out var buyingPrice) ||
                !TryReadPrice(quote, "Selling", out var sellingPrice) ||
                buyingPrice <= 0m || sellingPrice <= 0m)
            {
                throw new JsonException($"Truncgil '{product.ProviderSymbol}' için geçerli alış/satış fiyatı döndürmedi.");
            }

            prices.Add(product.Key, new GoldPrice(
                buyingPrice,
                sellingPrice,
                product.Currency,
                product.ProviderSymbol));
        }

        return new GoldPriceSnapshot(prices, DateTime.UtcNow, "Truncgil Finans");
    }

    private async Task<GoldPriceSnapshot> FetchPricesFromFallbackAsync(CancellationToken ct)
    {
        using var response = await _http.GetAsync(FallbackGoldUrl, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        if (!document.RootElement.TryGetProperty("price", out var priceEl) ||
            !priceEl.TryGetDecimal(out var ounceUsd) ||
            ounceUsd <= 0m)
        {
            throw new JsonException("Yedek ons altın fiyatı okunamadı.");
        }

        var rates = await _rateProvider.GetRatesAsync(ct);
        if (!rates.TryGetValue("USD", out var usdRate) || usdRate.ForexSelling <= 0m)
        {
            throw new InvalidOperationException("TCMB Dolar kuru alınamadı.");
        }

        var usdBuy = usdRate.ForexBuying > 0m ? usdRate.ForexBuying : usdRate.ForexSelling;
        var usdSell = usdRate.ForexSelling;

        var hasBuy = Math.Round((ounceUsd / 31.1034768m) * usdBuy, 2, MidpointRounding.AwayFromZero);
        var hasSell = Math.Round((ounceUsd / 31.1034768m) * usdSell * 1.005m, 2, MidpointRounding.AwayFromZero);

        var multipliers = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)
        {
            ["gram"] = 0.995m,
            ["has"] = 1.000m,
            ["22ayar"] = 0.916m,
            ["18ayar"] = 0.750m,
            ["14ayar"] = 0.585m,
            ["quarter"] = 1.606m,
            ["half"] = 3.212m,
            ["full"] = 6.424m,
            ["cumhuriyet"] = 6.608m,
            ["ata"] = 6.608m,
            ["resat"] = 6.608m,
            ["hamit"] = 6.608m,
            ["gremse"] = 16.060m,
            ["ikibucuk"] = 16.060m,
            ["besli"] = 33.040m
        };

        var prices = new Dictionary<string, GoldPrice>(StringComparer.OrdinalIgnoreCase);
        foreach (var product in Products)
        {
            if (product.Key == "ounce")
            {
                prices.Add(product.Key, new GoldPrice(
                    Math.Round(ounceUsd, 2, MidpointRounding.AwayFromZero),
                    Math.Round(ounceUsd * 1.002m, 2, MidpointRounding.AwayFromZero),
                    "USD",
                    product.ProviderSymbol));
                continue;
            }

            var mult = multipliers.GetValueOrDefault(product.Key, 1m);
            prices.Add(product.Key, new GoldPrice(
                Math.Round(hasBuy * mult, 2, MidpointRounding.AwayFromZero),
                Math.Round(hasSell * mult, 2, MidpointRounding.AwayFromZero),
                "TRY",
                product.ProviderSymbol));
        }

        return new GoldPriceSnapshot(prices, DateTime.UtcNow, "TCMB + Uluslararası Ons");
    }

    private static bool TryReadPrice(JsonElement quote, string propertyName, out decimal price)
    {
        price = 0m;
        if (!quote.TryGetProperty(propertyName, out var value))
        {
            return false;
        }

        var text = value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
        return decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out price);
    }

    private sealed record GoldProduct(
        string Key,
        string DisplayName,
        string ProviderSymbol,
        string QuantityUnit,
        string Currency);

    private sealed record GoldPrice(
        decimal BuyingPrice,
        decimal SellingPrice,
        string Currency,
        string ProviderSymbol);

    private sealed record GoldPriceSnapshot(
        Dictionary<string, GoldPrice> Prices,
        DateTime FetchedAtUtc,
        string Source);

    private sealed record GoldPriceArchive(
        decimal? BuyingPrice,
        decimal? SellingPrice,
        string? Currency,
        string? ProviderSymbol,
        decimal? UnitPrice)
    {
        public GoldPrice ToPrice(GoldProduct product)
        {
            var legacyPrice = UnitPrice ?? 0m;
            return new GoldPrice(
                BuyingPrice ?? legacyPrice,
                SellingPrice ?? legacyPrice,
                Currency ?? product.Currency,
                ProviderSymbol ?? product.ProviderSymbol);
        }

        public static GoldPriceArchive From(GoldPrice price) =>
            new(price.BuyingPrice, price.SellingPrice, price.Currency, price.ProviderSymbol, null);
    }

    private static CalculateResponse Failure(string message) => new()
    {
        Success = false,
        ServiceType = "gold",
        SubCategory = string.Empty,
        Message = message
    };
}

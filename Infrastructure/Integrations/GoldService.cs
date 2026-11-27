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

    private static readonly TimeSpan PriceCacheLifetime = TimeSpan.FromMinutes(5);

    private static readonly decimal DefaultGram = 1m;
    private const decimal GramsPerOunce = 31.1034768m;
    private static readonly GoldProduct[] Products =
    {
        new("gram", "Gram Altın", "GRA", "gram", "TRY", 1m),
        new("ounce", "Ons Altın", "ONS", "ons", "TRY"),
        new("quarter", "Çeyrek Altın", "CEYREKALTIN", "adet", "TRY", 1.75m),
        new("half", "Yarım Altın", "YARIMALTIN", "adet", "TRY", 3.50m),
        new("full", "Tam Altın", "TAMALTIN", "adet", "TRY", 7.00m),
        new("cumhuriyet", "Cumhuriyet Altını", "CUMHURIYETALTINI", "adet", "TRY", 7.216m),
        new("gremse", "Gremse Altın", "GREMSEALTIN", "adet", "TRY", 17.50m),
        new("has", "Has Altın", "HAS", "gram", "TRY"),
        new("22ayar", "22 Ayar Bilezik", "YIA", "gram", "TRY"),
        new("resat", "Reşat Altın", "RESATALTIN", "adet", "TRY", 7.216m),
        new("ata", "Ata Altın", "ATAALTIN", "adet", "TRY", 7.216m),
        new("14ayar", "14 Ayar Altın", "14AYARALTIN", "gram", "TRY"),
        new("18ayar", "18 Ayar Altın", "18AYARALTIN", "gram", "TRY"),
        new("besli", "Beşli Altın", "BESLIALTIN", "adet", "TRY", 35.00m),
        new("hamit", "Hamit Altın", "HAMITALTIN", "adet", "TRY", 7.216m),
        new("ikibucuk", "İkibuçuk Altın", "IKIBUCUKALTIN", "adet", "TRY", 2.50m)
    };

    private readonly HttpClient _http;
    private readonly IExternalApiCache _cache;
    private readonly ISharedApiDataStore _sharedApiData;
    private readonly ILogger<GoldService> _logger;
    private static readonly JsonSerializerOptions ArchiveJsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions ReadArchiveJsonOptions = new(JsonSerializerDefaults.Web);

    public GoldService(
        HttpClient http,
        IExternalApiCache cache,
        ISharedApiDataStore sharedApiData,
        ILogger<GoldService> logger)
    {
        _http = http;
        _cache = cache;
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
                RequiresQuantity = !product.HasFixedWeight,
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

        var grams = product.HasFixedWeight ? product.FixedGram!.Value : DefaultGram;
        if (!product.HasFixedWeight && !string.IsNullOrWhiteSpace(request.Quantity))
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

        price = Math.Round(price, 2, MidpointRounding.AwayFromZero);

        if (price <= 0m)
        {
            return Failure("Altın fiyatı şu an bulunamadı.");
        }

        var unitPrice = product.HasFixedWeight
            ? Math.Round(price, 2, MidpointRounding.AwayFromZero)
            : Math.Round(price * grams, 2, MidpointRounding.AwayFromZero);

        var tlPrice = unitPrice;

        string resultValue;
        string resultLabel;
        string inputLabel;
        string inputValue;

        if (direction == "to-tl")
        {
            var tl = Math.Round(input * unitPrice, 2, MidpointRounding.AwayFromZero);
            resultValue = tl.ToString("0.##", CultureInfo.InvariantCulture);
            resultLabel = "TL";
            inputLabel = product.QuantityUnit;
            inputValue = !product.HasFixedWeight && grams != DefaultGram
                ? $"{grams.ToString("0.##", CultureInfo.InvariantCulture)} gr"
                : input.ToString("0.####", CultureInfo.InvariantCulture);
        }
        else if (direction == "to-count")
        {
            var count = decimal.Floor(input / tlPrice);
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
            Quantity = product.HasFixedWeight ? null : grams.ToString("0.##", CultureInfo.InvariantCulture),
            RawDetail = $"Alış {providerPrice.BuyingPrice.ToString("0.##", CultureInfo.InvariantCulture)} {providerPrice.Currency} · " +
                        $"Satış {providerPrice.SellingPrice.ToString("0.##", CultureInfo.InvariantCulture)} {providerPrice.Currency} · " +
                        $"Hesaplama {price.ToString("0.##", CultureInfo.InvariantCulture)} TL/{product.QuantityUnit}" +
                        (direction == "to-tl" && !product.HasFixedWeight && grams != DefaultGram
                            ? $" × {grams.ToString("0.##", CultureInfo.InvariantCulture)} gr"
                            : string.Empty) +
                        $" · {sourceLabel}",
            Message = "Başarılı"
        };
    }

    private async Task<GoldPriceSnapshot> GetPriceSnapshotAsync(CancellationToken ct)
    {
        return await _cache.GetOrCreateAsync(
                   "gold:snapshot:v3",
                   PriceCacheLifetime,
                   FetchAndArchivePricesAsync,
                   snapshot => snapshot.Prices.Count == Products.Length &&
                               snapshot.Prices.Values.All(price => price.BuyingPrice > 0m && price.SellingPrice > 0m),
                   ct)
               ?? throw new InvalidOperationException("Altın fiyatları şu an alınamıyor.");
    }

    private async Task<GoldPriceSnapshot?> FetchAndArchivePricesAsync(CancellationToken ct)
    {
        var snapshot = await FetchPricesFromTruncgilAsync(ct);

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

        var gram = prices["gram"];
        prices["ounce"] = new GoldPrice(
            Math.Round(gram.BuyingPrice * GramsPerOunce, 2, MidpointRounding.AwayFromZero),
            Math.Round(gram.SellingPrice * GramsPerOunce, 2, MidpointRounding.AwayFromZero),
            "TRY",
            "ONS");

        return new GoldPriceSnapshot(prices, DateTime.UtcNow, "Truncgil Finans");
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
        string Currency,
        decimal? FixedGram = null)
    {
        public bool HasFixedWeight => FixedGram.HasValue;
    }

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
            var unit = UnitPrice is > 0m ? UnitPrice.Value : 0m;
            var currency = product.Currency;
            var buying = BuyingPrice is > 0m ? BuyingPrice.Value : unit;
            var selling = SellingPrice is > 0m ? SellingPrice.Value : unit;
            return new GoldPrice(
                Math.Round(buying, 2, MidpointRounding.AwayFromZero),
                Math.Round(selling, 2, MidpointRounding.AwayFromZero),
                currency,
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

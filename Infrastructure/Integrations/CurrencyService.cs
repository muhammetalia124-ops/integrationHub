using IntegrationHub.Domain;
using System.Globalization;
using Microsoft.Extensions.Logging;

namespace IntegrationHub.Integrations;

public class CurrencyService : IIntegrationService
{
    public string ServiceType => "currency";
    public string DisplayName => "Döviz";
    public string Icon => "currency";
    public bool IsEnabled => true;

    private static readonly string[] Supported =
    {
        "USD", "EUR", "GBP", "CHF", "SEK", "DKK", "NOK", "JPY", "SAR", "KWD", "AUD",
        "CAD", "RUB", "CNY", "PKR", "QAR", "KRW", "AZN", "AED", "RON", "KZT", "XDR"
    };
    private readonly IExchangeRateProvider _rateProvider;
    private readonly ILogger<CurrencyService> _logger;

    public CurrencyService(
        HttpClient _,
        IExchangeRateProvider rateProvider,
        ILogger<CurrencyService> logger)
    {
        _rateProvider = rateProvider;
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

        foreach (var code in Supported)
        {
            cat.SubCategories.Add(new SubCategoryMetadata
            {
                Key = code,
                DisplayName = CurrencyNames.TryGetValue(code, out var name) ? name : code,
                InputKind = "number",
                InputLabel = "Miktar (TL)",
                InputPlaceholder = "Örn. 1000",
                ResultLabel = "Sonuç",
                UsesCurrencyPair = true
            });
        }

        return cat;
    }

    public async Task<CalculateResponse> CalculateAsync(CalculateRequest request, int userId, CancellationToken ct = default)
    {
        if (!Supported.Contains(request.SubCategory))
        {
            return Failure("Desteklenmeyen döviz kodu.");
        }

        return await CalculatePairAsync(request, ct) ?? await CalculateToTl(request, ct);
    }

    private async Task<CalculateResponse?> CalculatePairAsync(CalculateRequest request, CancellationToken ct)
    {
        var sourceCode = NormalizeCurrencyCode(request.SourceCurrency);
        var targetCode = NormalizeCurrencyCode(request.TargetCurrency);
        if (string.IsNullOrEmpty(sourceCode) || string.IsNullOrEmpty(targetCode))
        {
            return null;
        }

        if (!IsSupported(sourceCode) || !IsSupported(targetCode))
        {
            return Failure("Desteklenmeyen para birimi seçildi.");
        }

        if (string.Equals(sourceCode, targetCode, StringComparison.OrdinalIgnoreCase))
        {
            return Failure("Aynı para birimleri arasında dönüşüm yapılamaz.");
        }

        if (!decimal.TryParse(request.InputValue, NumberStyles.Any, CultureInfo.InvariantCulture, out var amount) || amount <= 0)
        {
            return Failure("Geçerli bir miktar girin.");
        }

        IReadOnlyDictionary<string, CurrencyRate> rates;
        try
        {
            rates = await _rateProvider.GetRatesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TCMB döviz kurları alınamadı.");
            return Failure("Kur servisine ulaşılamadı. Lütfen tekrar deneyin.");
        }

        var sourceRate = GetSellingRate(rates, sourceCode);
        if (sourceRate <= 0m)
        {
            return Failure($"{sourceCode} kuru şu an bulunamadı.");
        }

        var targetRate = 1m;
        if (!string.Equals(targetCode, TlCode, StringComparison.OrdinalIgnoreCase))
        {
            targetRate = GetSellingRate(rates, targetCode);
            if (targetRate <= 0m)
            {
                return Failure($"{targetCode} kuru şu an bulunamadı.");
            }
        }

        var result = Math.Round(amount * (sourceRate / targetRate), 4, MidpointRounding.AwayFromZero);
        var pairLabel = $"{sourceCode} → {targetCode}";

        return new CalculateResponse
        {
            Success = true,
            ServiceType = ServiceType,
            SubCategory = sourceCode,
            InputLabel = $"Miktar ({sourceCode})",
            InputValue = amount.ToString("0.####", CultureInfo.InvariantCulture),
            ResultLabel = pairLabel,
            ResultValue = result.ToString("0.####", CultureInfo.InvariantCulture),
            RawDetail = $"1 {sourceCode} = {(sourceRate / targetRate).ToString("0.####", CultureInfo.InvariantCulture)} {targetCode}" +
                         $" · {sourceCode} satış {sourceRate.ToString("0.####", CultureInfo.InvariantCulture)} TL" +
                         (string.Equals(targetCode, TlCode, StringComparison.OrdinalIgnoreCase)
                             ? string.Empty
                             : $" · {targetCode} satış {targetRate.ToString("0.####", CultureInfo.InvariantCulture)} TL"),
            Message = "Başarılı"
        };
    }

    private async Task<CalculateResponse> CalculateToTl(CalculateRequest request, CancellationToken ct)
    {
        var code = request.SubCategory;
        if (!Supported.Contains(code))
        {
            return Failure("Desteklenmeyen döviz kodu.");
        }

        if (!decimal.TryParse(request.InputValue, NumberStyles.Any, CultureInfo.InvariantCulture, out var tlAmount) || tlAmount <= 0)
        {
            return Failure("Geçerli bir TL miktarı girin.");
        }

        IReadOnlyDictionary<string, CurrencyRate> rates;
        try
        {
            rates = await _rateProvider.GetRatesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TCMB döviz kurları alınamadı.");
            return Failure("Kur servisine ulaşılamadı. Lütfen tekrar deneyin.");
        }

        if (!rates.TryGetValue(code, out var currency))
        {
            return Failure($"{code} kuru şu an bulunamadı.");
        }

        var sellingRate = currency.ForexSelling;
        if (sellingRate <= 0m && code == "XDR")
        {
            sellingRate = currency.ForexBuying;
        }

        if (sellingRate <= 0m)
        {
            return Failure($"{code} kuru şu an bulunamadı.");
        }

        var amount = Math.Round(tlAmount / sellingRate, 4, MidpointRounding.AwayFromZero);

        return new CalculateResponse
        {
            Success = true,
            ServiceType = ServiceType,
            SubCategory = code,
            InputLabel = "Miktar (TL)",
            InputValue = tlAmount.ToString("0.##", CultureInfo.InvariantCulture),
            ResultLabel = $"{currency.Name} ({code})",
            ResultValue = amount.ToString("0.####", CultureInfo.InvariantCulture),
            RawDetail = $"1 {code} = {sellingRate.ToString("0.####", CultureInfo.InvariantCulture)} TL (satış)",
            Message = "Başarılı"
        };
    }

    private static CalculateResponse Failure(string message) => new()
    {
        Success = false,
        ServiceType = "currency",
        SubCategory = string.Empty,
        Message = message
    };

    private const string TlCode = "TRY";

    private static bool IsSupported(string code) =>
        string.Equals(code, TlCode, StringComparison.OrdinalIgnoreCase) || Supported.Contains(code);

    private static string? NormalizeCurrencyCode(string? code)
    {
        var trimmed = code?.Trim().ToUpperInvariant();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private static decimal GetSellingRate(IReadOnlyDictionary<string, CurrencyRate> rates, string code)
    {
        if (string.Equals(code, TlCode, StringComparison.OrdinalIgnoreCase))
        {
            return 1m;
        }

        if (!rates.TryGetValue(code, out var currency))
        {
            return 0m;
        }

        return currency.ForexSelling > 0m ? currency.ForexSelling : currency.ForexBuying;
    }

    private static readonly IReadOnlyDictionary<string, string> CurrencyNames =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["USD"] = "Amerikan Doları",
            ["EUR"] = "Euro",
            ["GBP"] = "İngiliz Sterlini",
            ["CHF"] = "İsviçre Frangı",
            ["SEK"] = "İsveç Kronu",
            ["DKK"] = "Danimarka Kronu",
            ["NOK"] = "Norveç Kronu",
            ["JPY"] = "Japon Yeni",
            ["SAR"] = "Suudi Arabistan Riyali",
            ["KWD"] = "Kuveyt Dinarı",
            ["AUD"] = "Avustralya Doları",
            ["CAD"] = "Kanada Doları",
            ["RUB"] = "Rus Rublesi",
            ["CNY"] = "Çin Yuanı",
            ["PKR"] = "Pakistan Rupisi",
            ["QAR"] = "Katar Riyali",
            ["KRW"] = "Güney Kore Wonu",
            ["AZN"] = "Azerbaycan Manatı",
            ["AED"] = "BAE Dirhemi",
            ["RON"] = "Romen Leyi",
            ["KZT"] = "Kazakistan Tengesi",
            ["XDR"] = "Özel Çekme Hakkı"
        };
}

using IntegrationHub.Domain;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace IntegrationHub.Integrations;

public sealed class LocationLookupService : ILocationLookupService
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromDays(30);
    private static readonly string[] NominatimHosts =
    {
        "https://nominatim.openstreetmap.org/",
        "https://nominatim.openstreetmap.de/"
    };

    private readonly HttpClient _http;
    private readonly IExternalApiCache _cache;
    private readonly ILogger<LocationLookupService> _logger;

    public LocationLookupService(HttpClient http, IExternalApiCache cache, ILogger<LocationLookupService> logger)
    {
        _http = http;
        _cache = cache;
        _logger = logger;
    }

    public async Task<ResolvedLocationResponse?> ResolveAsync(
        double latitude,
        double longitude,
        CancellationToken ct)
    {
        var latitudeText = latitude.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        var longitudeText = longitude.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        var key = $"location:reverse:v3:{Hash($"{latitudeText},{longitudeText}")}";
        return await _cache.GetOrCreateAsync(
            key,
            CacheLifetime,
            token => ResolveFromProvidersAsync(latitudeText, longitudeText, token),
            result => result is not null,
            ct);
    }

    private async Task<ResolvedLocationResponse?> ResolveFromProvidersAsync(
        string latitude,
        string longitude,
        CancellationToken ct)
    {
        var resolved = await TryBigDataCloudAsync(latitude, longitude, ct);
        if (resolved is not null)
        {
            return resolved;
        }

        foreach (var host in NominatimHosts)
        {
            resolved = await TryNominatimAsync(host, latitude, longitude, ct);
            if (resolved is not null)
            {
                return resolved;
            }
        }

        return null;
    }

    private async Task<ResolvedLocationResponse?> TryBigDataCloudAsync(
        string latitude,
        string longitude,
        CancellationToken ct)
    {
        try
        {
            var url = $"https://api.bigdatacloud.net/data/reverse-geocode-client?latitude={latitude}&longitude={longitude}&localityLanguage=tr";
            using var response = await _http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("BigDataCloud konum servisi {Status} döndürdü.", (int)response.StatusCode);
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = document.RootElement;

            var city = FirstNonEmpty(
                GetString(root, "principalSubdivision"),
                GetString(root, "city"),
                GetString(root, "locality"));
            var district = FirstNonEmpty(
                GetString(root, "locality"),
                GetString(root, "city"),
                GetString(root, "localityInfo"));

            if (!string.IsNullOrWhiteSpace(city) && string.Equals(city, district, StringComparison.OrdinalIgnoreCase))
            {
                district = GetNestedLocality(root) ?? district;
            }

            city = NormalizeCityName(city);
            if (string.IsNullOrWhiteSpace(city))
            {
                return null;
            }

            district = string.IsNullOrWhiteSpace(district) || string.Equals(district, city, StringComparison.OrdinalIgnoreCase)
                ? "Merkez"
                : district!;

            return new ResolvedLocationResponse(city!, district);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "BigDataCloud konum servisi kullanılamadı.");
            return null;
        }
    }

    private async Task<ResolvedLocationResponse?> TryNominatimAsync(
        string host,
        string latitude,
        string longitude,
        CancellationToken ct)
    {
        try
        {
            var url = $"{host}reverse?format=jsonv2&lat={latitude}&lon={longitude}&zoom=10&addressdetails=1&accept-language=tr";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("IntegrationHub/1.0 (+https://integrationhub.onrender.com)");
            request.Headers.AcceptLanguage.ParseAdd("tr");

            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Nominatim ({Host}) {Status} döndürdü.", host, (int)response.StatusCode);
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            if (!document.RootElement.TryGetProperty("address", out var address))
            {
                return null;
            }

            var city = GetAddressValue(address, "province")
                       ?? GetAddressValue(address, "city")
                       ?? GetAddressValue(address, "state")
                       ?? GetAddressValue(address, "town")
                       ?? GetAddressValue(address, "village");

            var district = GetAddressValue(address, "town")
                           ?? GetAddressValue(address, "city_district")
                           ?? GetAddressValue(address, "district")
                           ?? GetAddressValue(address, "municipality")
                           ?? GetAddressValue(address, "county")
                           ?? GetAddressValue(address, "suburb");

            if (string.IsNullOrWhiteSpace(city) && string.IsNullOrWhiteSpace(district))
            {
                return null;
            }

            city = NormalizeCityName(city) ?? district!;
            district ??= "Merkez";

            return new ResolvedLocationResponse(city, district);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Nominatim konum servisi ({Host}) kullanılamadı.", host);
            return null;
        }
    }

    private static string? NormalizeCityName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Equals("Istanbul", StringComparison.OrdinalIgnoreCase) ? "İstanbul" : trimmed;
    }

    private static string? GetNestedLocality(JsonElement root)
    {
        if (!root.TryGetProperty("localityInfo", out var info) ||
            !info.TryGetProperty("administrative", out var administrative) ||
            administrative.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        string? city = null;
        foreach (var entry in administrative.EnumerateArray())
        {
            if (!entry.TryGetProperty("name", out var nameElement) || nameElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var name = nameElement.GetString();
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            if (entry.TryGetProperty("adminLevel", out var levelElement) && levelElement.TryGetInt32(out var level) && level == 4)
            {
                return name;
            }

            city ??= name;
        }

        return city;
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string? GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? GetAddressValue(JsonElement address, string name)
    {
        return address.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }
}

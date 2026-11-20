using IntegrationHub.Domain;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;

namespace IntegrationHub.Integrations;

public sealed class LocationLookupService : ILocationLookupService
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromDays(30);
    private readonly HttpClient _http;
    private readonly IExternalApiCache _cache;

    public LocationLookupService(HttpClient http, IExternalApiCache cache)
    {
        _http = http;
        _cache = cache;
    }

    public async Task<ResolvedLocationResponse?> ResolveAsync(
        double latitude,
        double longitude,
        CancellationToken ct)
    {
        var latitudeText = latitude.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        var longitudeText = longitude.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        var key = $"location:reverse:v2:{Hash($"{latitudeText},{longitudeText}")}";
        return await _cache.GetOrCreateAsync(
            key,
            CacheLifetime,
            token => ResolveFromProviderAsync(latitudeText, longitudeText, token),
            result => result is not null,
            ct);
    }

    private async Task<ResolvedLocationResponse?> ResolveFromProviderAsync(
        string latitude,
        string longitude,
        CancellationToken ct)
    {
        var url = $"reverse?format=jsonv2&lat={latitude}&lon={longitude}&zoom=10&addressdetails=1";
        using var response = await _http.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode)
        {
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

        city ??= district!;
        district ??= "Merkez";

        return new ResolvedLocationResponse(city, district);
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string? GetAddressValue(JsonElement address, string name)
    {
        return address.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }
}

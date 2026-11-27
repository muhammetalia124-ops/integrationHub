namespace IntegrationHub.Domain;

public class JwtSettings
{
    public string Issuer { get; set; } = string.Empty;
    public string Audience { get; set; } = string.Empty;
    public string SecretKey { get; set; } = string.Empty;

    public int AccessTokenMinutes { get; set; } = 15;

    public int RefreshTokenDays { get; set; } = 7;
}

public class AccountSettings
{
    public static readonly int DefaultUsernameChangeIntervalDays = 30;

    public int UsernameChangeIntervalDays { get; set; } = DefaultUsernameChangeIntervalDays;
}

public class EmailSettings
{
    public string Provider { get; set; } = EmailProviders.Auto;
    public string ApiKey { get; set; } = string.Empty;

    public string FromEmail { get; set; } = string.Empty;
    public string FromName { get; set; } = "IntegrationHub";

    public string PublicAppUrl { get; set; } = "http://localhost:5080";

    public int VerificationCodeMinutes { get; set; } = 30;

    /// <summary>
    /// Seçili sağlayıcıya (auto ise Brevo'ya öncelik verilerek) gönderim yapılabilir mi.
    /// </summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ApiKey) &&
        !string.IsNullOrWhiteSpace(FromEmail) &&
        EmailProviders.IsSupported(Provider);

    /// <summary>
    /// Etkin HTTP API sağlayıcısı: `auto` için Brevo, aksi halde `Provider` değeri.
    /// </summary>
    public string ResolvedProvider =>
        string.Equals(Provider, EmailProviders.Resend, StringComparison.OrdinalIgnoreCase)
            ? EmailProviders.Resend
            : EmailProviders.Brevo;
}

public static class EmailProviders
{
    public const string Auto = "auto";
    public const string Resend = "resend";
    public const string Brevo = "brevo";

    public static bool IsSupported(string? provider) =>
        provider is not null &&
        (provider.Equals(Auto, StringComparison.OrdinalIgnoreCase) ||
         provider.Equals(Resend, StringComparison.OrdinalIgnoreCase) ||
         provider.Equals(Brevo, StringComparison.OrdinalIgnoreCase));
}

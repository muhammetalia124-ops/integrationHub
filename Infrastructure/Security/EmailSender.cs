using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using IntegrationHub.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IntegrationHub.Infrastructure.Security;

public sealed class EmailSender : IEmailSender
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly EmailSettings _settings;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<EmailSender> _logger;

    public EmailSender(
        IOptions<EmailSettings> settings,
        IHttpClientFactory httpClientFactory,
        ILogger<EmailSender> logger)
    {
        _settings = settings.Value;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public bool IsConfigured => _settings.IsConfigured;

    public Task SendVerificationEmailAsync(
        User user,
        string? link,
        string code,
        DateTime expiresAtUtc,
        CancellationToken ct) =>
        SendAsync(
            user.Email,
            "IntegrationHub - E-posta Doğrulama",
            EmailTemplates.Verification(user, link, code, expiresAtUtc),
            ct);

    public Task SendPasswordResetEmailAsync(
        User user,
        string? link,
        string code,
        DateTime expiresAtUtc,
        CancellationToken ct) =>
        SendAsync(
            user.Email,
            "IntegrationHub - Şifre Sıfırlama",
            EmailTemplates.PasswordReset(user, link, code, expiresAtUtc),
            ct);

    public Task SendPasswordChangedNoticeAsync(User user, CancellationToken ct) =>
        SendAsync(
            user.Email,
            "IntegrationHub - Şifreniz Güncellendi",
            EmailTemplates.PasswordChanged(user),
            ct);

    /// <summary>
    /// E-postalar yalnızca HTTP API üzerinden gönderilir (Brevo veya Resend).
    /// </summary>
    public async Task SendAsync(string toEmail, string subject, string body, CancellationToken ct)
    {
        if (!_settings.IsConfigured)
        {
            _logger.LogWarning(
                "E-posta gönderilemedi: API anahtarı veya gönderen adresi tanımlı değil. {To} / {Subject}",
                toEmail,
                subject);
            return;
        }

        if (_settings.ResolvedProvider == EmailProviders.Resend)
        {
            await SendWithResendAsync(toEmail, subject, body, ct);
            return;
        }

        await SendWithBrevoAsync(toEmail, subject, body, ct);
    }

    private async Task SendWithResendAsync(string toEmail, string subject, string body, CancellationToken ct)
    {
        var client = CreateClient("resend", "https://api.resend.com");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _settings.ApiKey.Trim());

        var payload = new
        {
            from = $"{_settings.FromName} <{_settings.FromEmail}>",
            to = new[] { toEmail },
            subject,
            html = body
        };

        using var response = await client.PostAsJsonAsync("/emails", payload, JsonOptions, ct);
        await EnsureSuccessAsync(EmailProviders.Resend, response, toEmail, ct);
        _logger.LogInformation("E-posta gönderildi (resend): {To} / {Subject}", toEmail, subject);
    }

    private async Task SendWithBrevoAsync(string toEmail, string subject, string body, CancellationToken ct)
    {
        var client = CreateClient("brevo", "https://api.brevo.com");
        client.DefaultRequestHeaders.TryAddWithoutValidation("api-key", _settings.ApiKey.Trim());

        var payload = new
        {
            sender = new { name = _settings.FromName, email = _settings.FromEmail },
            to = new[] { new { email = toEmail } },
            subject,
            htmlContent = body
        };

        using var response = await client.PostAsJsonAsync("/v3/smtp/email", payload, JsonOptions, ct);
        await EnsureSuccessAsync(EmailProviders.Brevo, response, toEmail, ct);
        _logger.LogInformation("E-posta gönderildi (brevo): {To} / {Subject}", toEmail, subject);
    }

    private HttpClient CreateClient(string name, string baseAddress)
    {
        var client = _httpClientFactory.CreateClient(name);
        client.BaseAddress = new Uri(baseAddress);
        client.Timeout = TimeSpan.FromSeconds(20);
        return client;
    }

    private async Task EnsureSuccessAsync(
        string provider,
        HttpResponseMessage response,
        string toEmail,
        CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var detail = await response.Content.ReadAsStringAsync(ct);
        _logger.LogError(
            "E-posta API'si hata döndürdü: {Provider} {Status} {To} -> {Detail}",
            provider,
            (int)response.StatusCode,
            toEmail,
            detail);
        throw new HttpRequestException(
            $"E-posta API'si {(int)response.StatusCode} döndürdü: {detail}");
    }
}

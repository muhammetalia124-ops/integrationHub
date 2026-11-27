using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IntegrationHub.Domain;

public sealed record EmailDispatchResult(bool EmailSent, string? DevelopmentCode = null);

public sealed class AppEnvironmentInfo
{
    public string Name { get; set; } = string.Empty;
    public bool IsDevelopment => string.Equals(Name, "Development", StringComparison.OrdinalIgnoreCase);
}

public sealed class AccountSecurityService
{
    private const int CodeAttemptLimit = 5;

    public const string GenericResetMessage =
        "Şifre sıfırlama bağlantısı gönderildi. E-posta kutunuzu (spam klasörü dahil) kontrol edin.";

    private readonly IUserRepository _users;
    private readonly IAuthVerificationCodeRepository _codes;
    private readonly IRefreshTokenRepository _refreshTokens;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IAuthTokenGenerator _tokenGenerator;
    private readonly IEmailSender _email;
    private readonly LoginThrottle _loginThrottle;
    private readonly EmailSettings _emailSettings;
    private readonly AppEnvironmentInfo _environment;
    private readonly ITimeProvider _time;
    private readonly ILogger<AccountSecurityService> _logger;

    public AccountSecurityService(
        IUserRepository users,
        IAuthVerificationCodeRepository codes,
        IRefreshTokenRepository refreshTokens,
        IPasswordHasher passwordHasher,
        IAuthTokenGenerator tokenGenerator,
        IEmailSender email,
        LoginThrottle loginThrottle,
        IOptions<EmailSettings> emailSettings,
        AppEnvironmentInfo environment,
        ITimeProvider time,
        ILogger<AccountSecurityService> logger)
    {
        _users = users;
        _codes = codes;
        _refreshTokens = refreshTokens;
        _passwordHasher = passwordHasher;
        _tokenGenerator = tokenGenerator;
        _email = email;
        _loginThrottle = loginThrottle;
        _emailSettings = emailSettings.Value;
        _environment = environment;
        _time = time;
        _logger = logger;
    }

    public bool IsEmailConfigured => _email.IsConfigured;

    public async Task<EmailDispatchResult> SendRegistrationVerificationAsync(User user, CancellationToken ct)
    {
        var issued = Issue(user, VerificationPurposes.EmailVerification);
        await _codes.DeleteAllForEmailAsync(user.Email, VerificationPurposes.EmailVerification, ct);
        await _codes.AddAsync(issued.Code, ct);

        var sent = await TrySendAsync(
            () => _email.SendVerificationEmailAsync(user, issued.Link, issued.RawCode, issued.ExpiresAtUtc, ct),
            user.Email);

        LogDevelopmentCode(user.Email, issued.RawCode, sent);
        return new EmailDispatchResult(
            sent,
            !sent && _environment.IsDevelopment ? issued.RawCode : null);
    }

    private async Task<bool> TrySendAsync(Func<Task> send, string email)
    {
        try
        {
            await send();
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "E-posta gönderilemedi: {Email}", email);
            return false;
        }
    }

    private void LogDevelopmentCode(string email, string code, bool sent)
    {
        if (!sent && _environment.IsDevelopment)
        {
            _logger.LogWarning("DEV doğrulama kodu: {Email} -> {Code}", email, code);
        }
    }

    public async Task<EmailDispatchResult> ResendVerificationAsync(string email, CancellationToken ct)
    {
        var user = await _users.GetByEmailAsync(email.Trim(), ct);
        if (user is null || !user.IsActive || user.IsEmailVerified)
        {
            return new EmailDispatchResult(true);
        }

        return await SendRegistrationVerificationAsync(user, ct);
    }

    public async Task<AuthOperationResult> VerifyEmailAsync(VerifyEmailRequest request, CancellationToken ct)
    {
        var user = await _users.GetByEmailAsync(request.Email.Trim(), ct);
        if (user is null || !user.IsActive)
        {
            return new AuthOperationResult(AuthOperationStatus.Invalid,
                Error: "Doğrulama işlemi tamamlanamadı. Bilgileri kontrol edip tekrar deneyin.");
        }

        if (user.IsEmailVerified)
        {
            return new AuthOperationResult(AuthOperationStatus.Success);
        }

        var code = await FindCodeAsync(user.Email, VerificationPurposes.EmailVerification, request.Code, request.Token, ct);
        if (code is null)
        {
            return new AuthOperationResult(AuthOperationStatus.Invalid,
                Error: "Doğrulama kodu geçersiz veya süresi dolmuş. Yeni bir doğrulama e-postası isteyin.");
        }

        code.ConsumedAtUtc = _time.UtcNow;
        await _codes.UpdateAsync(code, ct);

        user.IsEmailVerified = true;
        await _users.UpdateAsync(user, ct);
        await _codes.DeleteAllForEmailAsync(user.Email, VerificationPurposes.EmailVerification, ct);

        _logger.LogInformation("E-posta adresi doğrulandı: {Email}", user.Email);
        return new AuthOperationResult(AuthOperationStatus.Success);
    }

    public async Task<EmailDispatchResult> SendPasswordResetAsync(string rawEmail, CancellationToken ct)
    {
        var email = rawEmail.Trim();
        var user = await _users.GetByEmailAsync(email, ct);
        if (user is null || !user.IsActive)
        {
            _logger.LogInformation("Şifre sıfırlama talebi: kayıtlı olmayan veya etkin olmayan e-posta {Email}", email);
            return new EmailDispatchResult(true);
        }

        var issued = Issue(user, VerificationPurposes.PasswordReset);
        await _codes.DeleteAllForEmailAsync(user.Email, VerificationPurposes.PasswordReset, ct);
        await _codes.AddAsync(issued.Code, ct);

        var sent = await TrySendAsync(
            () => _email.SendPasswordResetEmailAsync(user, issued.Link, issued.RawCode, issued.ExpiresAtUtc, ct),
            user.Email);

        LogDevelopmentCode(user.Email, issued.RawCode, sent);
        return new EmailDispatchResult(sent, !sent && _environment.IsDevelopment ? issued.RawCode : null);
    }

    public async Task<AuthOperationResult> ResetPasswordAsync(ResetPasswordRequest request, CancellationToken ct)
    {
        var user = await _users.GetByEmailAsync(request.Email.Trim(), ct);
        if (user is null || !user.IsActive)
        {
            return new AuthOperationResult(AuthOperationStatus.Invalid,
                Error: "Sıfırlama bağlantısı geçersiz veya süresi dolmuş. Lütfen yeni bir bağlantı isteyin.");
        }

        var code = await FindCodeAsync(user.Email, VerificationPurposes.PasswordReset, request.Code, request.Token, ct);
        if (code is null)
        {
            return new AuthOperationResult(AuthOperationStatus.Invalid,
                Error: "Sıfırlama kodu geçersiz veya süresi dolmuş. Lütfen yeni bir bağlantı isteyin.");
        }

        if (_passwordHasher.Verify(request.NewPassword, user.PasswordHash))
        {
            return new AuthOperationResult(AuthOperationStatus.Invalid,
                Error: "Yeni şifreniz mevcut şifrenizle aynı olamaz. Lütfen farklı bir şifre belirleyin.");
        }

        code.ConsumedAtUtc = _time.UtcNow;
        await _codes.UpdateAsync(code, ct);

        user.PasswordHash = _passwordHasher.Hash(request.NewPassword);
        user.IsEmailVerified = true;
        await _users.UpdateAsync(user, ct);

        await _codes.DeleteAllForEmailAsync(user.Email, VerificationPurposes.PasswordReset, ct);
        await _refreshTokens.DeleteAllForUserAsync(user.Id, ct);
        _loginThrottle.Reset(user.Email);

        if (_email.IsConfigured)
        {
            await _email.SendPasswordChangedNoticeAsync(user, ct);
        }

        _logger.LogInformation("Şifre sıfırlandı: {Email}", user.Email);
        return new AuthOperationResult(AuthOperationStatus.Success);
    }

    private async Task<AuthVerificationCode?> FindCodeAsync(
        string email,
        string purpose,
        string? rawCode,
        string? rawToken,
        CancellationToken ct)
    {
        var code = await _codes.GetLatestActiveAsync(email, purpose, ct);
        if (code is null || code.Attempts >= CodeAttemptLimit)
        {
            return null;
        }

        string candidate;
        string stored;
        var parts = code.CodeHash.Split(':');
        if (!string.IsNullOrWhiteSpace(rawToken))
        {
            candidate = ShortTokenHash(rawToken.Trim());
            stored = parts[0];
        }
        else
        {
            candidate = ShortCodeHash(rawCode!.Trim());
            stored = parts.Length > 1 ? parts[1] : parts[0];
        }

        if (TokenHelpers.FixedTimeEquals(candidate, stored))
        {
            return code;
        }

        code.Attempts++;
        await _codes.UpdateAsync(code, ct);
        return null;
    }

    private IssuedVerification Issue(User user, string purpose)
    {
        var rawCode = _tokenGenerator.CreateVerificationCode();
        var rawToken = _tokenGenerator.CreateVerificationToken();
        var now = _time.UtcNow;
        var expiresAt = now.AddMinutes(Math.Max(5, _emailSettings.VerificationCodeMinutes));

        var entity = new AuthVerificationCode
        {
            Email = user.Email,
            Purpose = purpose,
            CodeHash = $"{ShortTokenHash(rawToken)}:{ShortCodeHash(rawCode)}",
            Attempts = 0,
            CreatedAtUtc = now,
            ExpiresAtUtc = expiresAt
        };

        var baseUrl = string.IsNullOrWhiteSpace(_emailSettings.PublicAppUrl)
            ? "http://localhost:5080"
            : _emailSettings.PublicAppUrl.TrimEnd('/');
        var action = purpose == VerificationPurposes.PasswordReset ? "reset-password" : "verify-email";
        var link =
            $"{baseUrl}/?action={action}&email={Uri.EscapeDataString(user.Email)}&token={Uri.EscapeDataString(rawToken)}";

        return new IssuedVerification(entity, rawCode, link, expiresAt);
    }

    private string ShortTokenHash(string token) => _tokenGenerator.HashToken(token)[..32];

    private static string ShortCodeHash(string code) => TokenHelpers.Hash(code).ToUpperInvariant()[..24];

    private static class TokenHelpers
    {
        public static string Hash(string value) =>
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));

        public static bool FixedTimeEquals(string a, string b) =>
            a.Length == b.Length &&
            System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(a),
                System.Text.Encoding.UTF8.GetBytes(b));
    }

    private sealed record IssuedVerification(
        AuthVerificationCode Code,
        string RawCode,
        string Link,
        DateTime ExpiresAtUtc);
}

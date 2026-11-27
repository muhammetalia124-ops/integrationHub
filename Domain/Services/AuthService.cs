using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IntegrationHub.Domain;

public enum AuthOperationStatus
{
    Success,
    Conflict,
    Unauthorized,
    Locked,
    Invalid,
    EmailNotVerified
}

public sealed record AuthOperationResult(
    AuthOperationStatus Status,
    AuthResponse? Response = null,
    string? Error = null,
    int? RemainingAttempts = null);

public sealed record RegisterOperationResult(
    AuthOperationStatus Status,
    RegisterResponse? Response = null,
    string? Error = null);

public sealed class AuthService
{
    private const string LastAdminProtected = "Sistemde en az bir etkin yönetici bulunmalıdır.";

    private readonly IUserRepository _users;
    private readonly IUserActivityRepository _activities;
    private readonly IRefreshTokenRepository _refreshTokens;
    private readonly IPasswordHasher _passwordHasher;
    private readonly ITokenIssuer _tokenIssuer;
    private readonly IAuthTokenGenerator _tokenGenerator;
    private readonly AccountSecurityService _accountSecurity;
    private readonly LoginThrottle _loginThrottle;
    private readonly JwtSettings _jwt;
    private readonly AccountSettings _accountSettings;
    private readonly ITimeProvider _time;
    private readonly ILogger<AuthService> _logger;

    public AuthService(
        IUserRepository users,
        IUserActivityRepository activities,
        IRefreshTokenRepository refreshTokens,
        IPasswordHasher passwordHasher,
        ITokenIssuer tokenIssuer,
        IAuthTokenGenerator tokenGenerator,
        AccountSecurityService accountSecurity,
        LoginThrottle loginThrottle,
        IOptions<JwtSettings> jwt,
        IOptions<AccountSettings> accountSettings,
        ITimeProvider time,
        ILogger<AuthService> logger)
    {
        _users = users;
        _activities = activities;
        _refreshTokens = refreshTokens;
        _passwordHasher = passwordHasher;
        _tokenIssuer = tokenIssuer;
        _tokenGenerator = tokenGenerator;
        _accountSecurity = accountSecurity;
        _loginThrottle = loginThrottle;
        _jwt = jwt.Value;
        _accountSettings = accountSettings.Value;
        _time = time;
        _logger = logger;
    }

    public async Task<RegisterOperationResult> RegisterAsync(RegisterRequest request, CancellationToken ct)
    {
        var email = request.Email.Trim();
        if (await _users.AnyByEmailAsync(email, ct))
        {
            return new RegisterOperationResult(AuthOperationStatus.Conflict, Error: "Bu e-posta zaten kayıtlı.");
        }

        var user = new User
        {
            Email = email,
            Username = request.Username.Trim(),
            PasswordHash = _passwordHasher.Hash(request.Password),
            CreatedAt = _time.UtcNow,
            IsActive = true,
            IsEmailVerified = false
        };
        await _users.AddAsync(user, ct);

        EmailDispatchResult dispatch;
        try
        {
            dispatch = await _accountSecurity.SendRegistrationVerificationAsync(user, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Doğrulama e-postası gönderilemedi: {Email}", user.Email);
            dispatch = new EmailDispatchResult(false);
        }

        _logger.LogInformation("Yeni kullanıcı kaydı (doğrulama bekliyor): {Email}", user.Email);

        return new RegisterOperationResult(
            AuthOperationStatus.Success,
            new RegisterResponse
            {
                Email = user.Email,
                EmailSent = dispatch.EmailSent,
                DevVerificationCode = dispatch.DevelopmentCode,
                Message = dispatch.EmailSent
                    ? "Kayıt oluşturuldu. E-posta adresinize gönderilen doğrulama bağlantısı ile hesabınızı etkinleştirin."
                    : "Kayıt oluşturuldu. E-posta gönderimi yapılandırılmadığı için doğrulama kodunu geliştirme ortamında ekrandan alabilirsiniz."
            });
    }

    public async Task<AuthOperationResult> LoginAsync(LoginRequest request, CancellationToken ct)
    {
        var email = request.Email.Trim();

        if (_loginThrottle.IsBlocked(email))
        {
            return new AuthOperationResult(
                AuthOperationStatus.Locked,
                Error: "3 kez hatalı giriş yaptınız. Hesabınız 15 dakika boyunca bloke edildi.");
        }

        var user = await _users.GetByEmailAsync(email, ct);
        if (user is null || !user.IsActive || !_passwordHasher.Verify(request.Password, user.PasswordHash))
        {
            var state = _loginThrottle.RegisterFailure(email);
            if (state.BlockedUntilUtc.HasValue)
            {
                _logger.LogWarning("Hatalı giriş limiti aşıldı: {Email}", email);
                return new AuthOperationResult(
                    AuthOperationStatus.Locked,
                    Error: "3 kez hatalı giriş yaptınız. Hesabınız 15 dakika boyunca bloke edildi.");
            }

            var remaining = LoginThrottle.MaxAttempts - state.FailedAttempts;
            return new AuthOperationResult(
                AuthOperationStatus.Unauthorized,
                Error: $"E-posta veya şifre hatalı. Kalan hakkınız: {remaining}",
                RemainingAttempts: remaining);
        }

        if (!user.IsEmailVerified && !user.IsAdmin)
        {
            return new AuthOperationResult(
                AuthOperationStatus.EmailNotVerified,
                Error: "E-posta adresiniz henüz doğrulanmadı. Gelen kutunuza gönderilen bağlantı ile hesabınızı etkinleştirin veya doğrulama e-postasını yeniden isteyin.");
        }

        _loginThrottle.Reset(email);
        _logger.LogInformation("Kullanıcı giriş yaptı: {Email}", user.Email);
        return new AuthOperationResult(AuthOperationStatus.Success, await BuildAuthResponseAsync(user, true, ct));
    }

    public async Task<AuthOperationResult> GetCurrentUserAsync(int userId, CancellationToken ct)
    {
        var user = await _users.GetByIdAsync(userId, ct);
        if (user is null || !user.IsActive)
        {
            return new AuthOperationResult(AuthOperationStatus.Unauthorized, Error: "Kullanıcı hesabı etkin değil.");
        }

        return new AuthOperationResult(
            AuthOperationStatus.Success,
            await BuildAuthResponseAsync(user, includeRefreshToken: false, ct));
    }

    public async Task<AuthOperationResult> RefreshAsync(string refreshToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return new AuthOperationResult(AuthOperationStatus.Unauthorized, Error: "Yenileme anahtarı gerekli.");
        }

        var hash = _tokenGenerator.HashToken(refreshToken.Trim());
        var stored = await _refreshTokens.GetByHashAsync(hash, ct);
        if (stored is null)
        {
            return new AuthOperationResult(AuthOperationStatus.Unauthorized, Error: "Oturum yenilenemedi. Lütfen tekrar giriş yapın.");
        }

        if (stored.ExpiresAtUtc <= _time.UtcNow)
        {
            await _refreshTokens.DeleteAsync(stored, ct);
            return new AuthOperationResult(AuthOperationStatus.Unauthorized, Error: "Oturum süresi doldu. Lütfen tekrar giriş yapın.");
        }

        var user = await _users.GetByIdAsync(stored.UserId, ct);
        if (user is null || !user.IsActive)
        {
            await _refreshTokens.DeleteAllForUserAsync(stored.UserId, ct);
            return new AuthOperationResult(AuthOperationStatus.Unauthorized, Error: "Kullanıcı hesabı etkin değil.");
        }

        var replacement = CreateRefreshTokenEntity(user.Id);
        await _refreshTokens.DeleteAsync(stored, ct);
        await _refreshTokens.AddAsync(replacement.Entity, ct);

        _logger.LogInformation("Oturum yenilendi: {Email}", user.Email);
        return new AuthOperationResult(
            AuthOperationStatus.Success,
            await BuildAuthResponseAsync(user, includeRefreshToken: false, ct, replacement.RawToken, replacement.Entity.ExpiresAtUtc));
    }

    public async Task LogoutAsync(string? refreshToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return;
        }

        var hash = _tokenGenerator.HashToken(refreshToken.Trim());
        var stored = await _refreshTokens.GetByHashAsync(hash, ct);
        if (stored is null)
        {
            return;
        }

        await _refreshTokens.DeleteAsync(stored, ct);
    }

    public async Task<AuthOperationResult> ChangePasswordAsync(
        int userId,
        ChangePasswordRequest request,
        CancellationToken ct)
    {
        var user = await _users.GetByIdAsync(userId, ct);
        if (user is null || !user.IsActive)
        {
            return new AuthOperationResult(AuthOperationStatus.Unauthorized, Error: "Kullanıcı hesabı etkin değil.");
        }

        if (!_passwordHasher.Verify(request.CurrentPassword, user.PasswordHash))
        {
            return new AuthOperationResult(AuthOperationStatus.Unauthorized, Error: "Mevcut şifre hatalı.");
        }

        if (string.Equals(request.CurrentPassword, request.NewPassword, StringComparison.Ordinal))
        {
            return new AuthOperationResult(
                AuthOperationStatus.Invalid,
                Error: "Yeni şifreniz mevcut şifrenizle aynı olamaz. Lütfen farklı bir şifre belirleyin.");
        }

        user.PasswordHash = _passwordHasher.Hash(request.NewPassword);
        await _users.UpdateAsync(user, ct);
        await _refreshTokens.DeleteAllForUserAsync(user.Id, ct);

        _logger.LogInformation("Kullanıcı şifresini değiştirdi: {Email}", user.Email);
        return new AuthOperationResult(AuthOperationStatus.Success, await BuildAuthResponseAsync(user, true, ct));
    }

    public async Task<AuthOperationResult> ChangeUsernameAsync(
        int userId,
        ChangeUsernameRequest request,
        CancellationToken ct)
    {
        var user = await _users.GetByIdAsync(userId, ct);
        if (user is null || !user.IsActive)
        {
            return new AuthOperationResult(AuthOperationStatus.Unauthorized, Error: "Kullanıcı hesabı etkin değil.");
        }

        var newUsername = request.NewUsername.Trim();
        if (string.Equals(newUsername, user.Username, StringComparison.Ordinal))
        {
            return new AuthOperationResult(AuthOperationStatus.Invalid, Error: "Yeni kullanıcı adınız mevcut kullanıcı adınızla aynı olamaz.");
        }

        var now = _time.UtcNow;
        var availableAt = GetUsernameChangeAvailableAt(user);
        if (availableAt.HasValue && availableAt.Value > now)
        {
            var remaining = availableAt.Value - now;
            var days = (int)Math.Ceiling(remaining.TotalDays);
            return new AuthOperationResult(
                AuthOperationStatus.Invalid,
                Error: $"Kullanıcı adınızı {UsernameChangeIntervalDays} günde yalnızca bir kez değiştirebilirsiniz. " +
                       $"Yeni bir değişiklik için kalan süre: {days} gün ({(availableAt.Value).ToLocalTime():dd.MM.yyyy HH:mm}).");
        }

        user.Username = newUsername;
        user.UsernameChangedAt = now;
        await _users.UpdateAsync(user, ct);

        await _refreshTokens.DeleteExpiredForUserAsync(user.Id, ct);
        _logger.LogInformation("Kullanıcı adı değiştirildi: {Email} -> {Username}", user.Email, user.Username);

        return new AuthOperationResult(AuthOperationStatus.Success, await BuildAuthResponseAsync(user, true, ct));
    }

    public async Task<AuthOperationResult> DeleteAccountAsync(
        int userId,
        DeleteAccountRequest request,
        CancellationToken ct)
    {
        var user = await _users.GetByIdAsync(userId, ct);
        if (user is null)
        {
            return new AuthOperationResult(AuthOperationStatus.Unauthorized, Error: "Kullanıcı hesabı bulunamadı.");
        }

        if (!_passwordHasher.Verify(request.Password, user.PasswordHash))
        {
            return new AuthOperationResult(AuthOperationStatus.Unauthorized, Error: "Şifreniz hatalı. Hesabınız silinmedi.");
        }

        if (user.IsAdmin && user.IsActive && await _users.CountActiveAdminsAsync(ct) <= 1)
        {
            return new AuthOperationResult(AuthOperationStatus.Conflict, Error: LastAdminProtected);
        }

        await _refreshTokens.DeleteAllForUserAsync(user.Id, ct);
        await _activities.DeleteHistoryByUserIdAsync(user.Id, ct);
        await _users.DeleteAsync(user, ct);
        _loginThrottle.Reset(user.Email);

        _logger.LogInformation("Kullanıcı hesabını ve geçmişini sildi: {Email}", user.Email);
        return new AuthOperationResult(AuthOperationStatus.Success);
    }

    public async Task<AuthOperationResult> AutoLoginAsync(AutoLoginRequest request, CancellationToken ct)
    {
        var email = request.Email.Trim();
        var user = await _users.GetByEmailAsync(email, ct);
        if (user is not null && !user.IsActive)
        {
            return new AuthOperationResult(AuthOperationStatus.Unauthorized, Error: "Kullanıcı hesabı etkin değil.");
        }

        if (user is null)
        {
            user = new User
            {
                Email = email,
                Username = request.Username.Trim(),
                PasswordHash = _passwordHasher.Hash(request.Password),
                CreatedAt = _time.UtcNow,
                IsActive = true,
                IsEmailVerified = true
            };
            await _users.AddAsync(user, ct);
            _logger.LogInformation("Bütçe entegrasyonu için yeni kullanıcı oluşturuldu: {Email}", user.Email);
            return new AuthOperationResult(AuthOperationStatus.Success, await BuildAuthResponseAsync(user, true, ct));
        }

        if (!_passwordHasher.Verify(request.Password, user.PasswordHash))
        {
            return new AuthOperationResult(AuthOperationStatus.Unauthorized, Error: "E-posta veya şifre hatalı.");
        }

        _logger.LogInformation("Bütçe entegrasyonu otomatik girişi: {Email}", user.Email);
        return new AuthOperationResult(AuthOperationStatus.Success, await BuildAuthResponseAsync(user, true, ct));
    }

    public Task<bool> IsActiveAdminAsync(int userId, CancellationToken ct) =>
        _users.IsActiveAdminAsync(userId, ct);

    private int UsernameChangeIntervalDays =>
        _accountSettings.UsernameChangeIntervalDays > 0
            ? _accountSettings.UsernameChangeIntervalDays
            : AccountSettings.DefaultUsernameChangeIntervalDays;

    private DateTime? GetUsernameChangeAvailableAt(User user) =>
        user.UsernameChangedAt?.AddDays(UsernameChangeIntervalDays);

    private async Task<AuthResponse> BuildAuthResponseAsync(
        User user,
        bool includeRefreshToken,
        CancellationToken ct,
        string? refreshToken = null,
        DateTime? refreshTokenExpiresAt = null)
    {
        if (includeRefreshToken && refreshToken is null)
        {
            var issued = CreateRefreshTokenEntity(user.Id);
            await _refreshTokens.AddAsync(issued.Entity, ct);
            refreshToken = issued.RawToken;
            refreshTokenExpiresAt = issued.Entity.ExpiresAtUtc;
        }

        var expiresAt = _time.UtcNow.AddMinutes(_jwt.AccessTokenMinutes);
        var availableAt = GetUsernameChangeAvailableAt(user);

        return new AuthResponse
        {
            UserId = user.Id,
            Username = user.Username,
            Email = user.Email,
            Token = _tokenIssuer.CreateToken(user, expiresAt),
            ExpiresAt = expiresAt,
            IsAdmin = user.IsAdmin,
            RefreshToken = refreshToken,
            RefreshTokenExpiresAt = refreshTokenExpiresAt,
            IsEmailVerified = user.IsEmailVerified,
            UsernameChangedAt = user.UsernameChangedAt,
            UsernameChangeAvailableAt = availableAt,
            CanChangeUsername = !availableAt.HasValue || availableAt.Value <= _time.UtcNow,
            UsernameChangeIntervalDays = UsernameChangeIntervalDays
        };
    }

    private (RefreshToken Entity, string RawToken) CreateRefreshTokenEntity(int userId)
    {
        var raw = _tokenGenerator.CreateRefreshToken();
        var now = _time.UtcNow;
        return (new RefreshToken
        {
            UserId = userId,
            TokenHash = _tokenGenerator.HashToken(raw),
            CreatedAtUtc = now,
            ExpiresAtUtc = now.AddDays(Math.Max(1, _jwt.RefreshTokenDays))
        }, raw);
    }
}

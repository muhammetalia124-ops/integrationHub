using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IntegrationHub.Domain;

public enum AuthOperationStatus
{
    Success,
    Conflict,
    Unauthorized
}

public sealed record AuthOperationResult(
    AuthOperationStatus Status,
    AuthResponse? Response = null,
    string? Error = null);

public sealed class AuthService
{
    private readonly IUserRepository _users;
    private readonly IPasswordHasher _passwordHasher;
    private readonly ITokenIssuer _tokenIssuer;
    private readonly JwtSettings _jwt;
    private readonly ILogger<AuthService> _logger;

    public AuthService(
        IUserRepository users,
        IPasswordHasher passwordHasher,
        ITokenIssuer tokenIssuer,
        IOptions<JwtSettings> jwt,
        ILogger<AuthService> logger)
    {
        _users = users;
        _passwordHasher = passwordHasher;
        _tokenIssuer = tokenIssuer;
        _jwt = jwt.Value;
        _logger = logger;
    }

    public async Task<AuthOperationResult> RegisterAsync(RegisterRequest request, CancellationToken ct)
    {
        var email = request.Email.Trim();
        if (await _users.AnyByEmailAsync(email, ct))
        {
            return new AuthOperationResult(AuthOperationStatus.Conflict, Error: "Bu e-posta zaten kayıtlı.");
        }

        var user = new User
        {
            Email = email,
            Username = request.Username.Trim(),
            PasswordHash = _passwordHasher.Hash(request.Password),
            CreatedAt = DateTime.UtcNow,
            IsActive = true
        };
        await _users.AddAsync(user, ct);

        _logger.LogInformation("Yeni kullanıcı kaydı: {Email}", user.Email);
        return new AuthOperationResult(AuthOperationStatus.Success, BuildAuthResponse(user));
    }

    public async Task<AuthOperationResult> LoginAsync(LoginRequest request, CancellationToken ct)
    {
        var email = request.Email.Trim();
        var user = await _users.GetByEmailAsync(email, ct);
        if (user is null || !user.IsActive || !_passwordHasher.Verify(request.Password, user.PasswordHash))
        {
            return new AuthOperationResult(AuthOperationStatus.Unauthorized, Error: "E-posta veya şifre hatalı.");
        }

        _logger.LogInformation("Kullanıcı giriş yaptı: {Email}", user.Email);
        return new AuthOperationResult(AuthOperationStatus.Success, BuildAuthResponse(user));
    }

    public async Task<AuthOperationResult> GetCurrentUserAsync(int userId, CancellationToken ct)
    {
        var user = await _users.GetByIdAsync(userId, ct);
        if (user is null || !user.IsActive)
        {
            return new AuthOperationResult(AuthOperationStatus.Unauthorized, Error: "Kullanıcı hesabı etkin değil.");
        }

        return new AuthOperationResult(AuthOperationStatus.Success, BuildAuthResponse(user));
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

        user.PasswordHash = _passwordHasher.Hash(request.NewPassword);
        await _users.UpdateAsync(user, ct);

        _logger.LogInformation("Kullanıcı şifresini değiştirdi: {Email}", user.Email);
        return new AuthOperationResult(AuthOperationStatus.Success, BuildAuthResponse(user));
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
                CreatedAt = DateTime.UtcNow,
                IsActive = true
            };
            await _users.AddAsync(user, ct);
            _logger.LogInformation("Bütçe entegrasyonu için yeni kullanıcı oluşturuldu: {Email}", user.Email);
            return new AuthOperationResult(AuthOperationStatus.Success, BuildAuthResponse(user));
        }

        if (!_passwordHasher.Verify(request.Password, user.PasswordHash))
        {
            return new AuthOperationResult(AuthOperationStatus.Unauthorized, Error: "E-posta veya şifre hatalı.");
        }

        _logger.LogInformation("Bütçe entegrasyonu otomatik girişi: {Email}", user.Email);
        return new AuthOperationResult(AuthOperationStatus.Success, BuildAuthResponse(user));
    }

    public Task<bool> IsActiveAdminAsync(int userId, CancellationToken ct) =>
        _users.IsActiveAdminAsync(userId, ct);

    private AuthResponse BuildAuthResponse(User user)
    {
        var expiresAt = DateTime.UtcNow.AddMinutes(_jwt.AccessTokenMinutes);
        return new AuthResponse
        {
            UserId = user.Id,
            Username = user.Username,
            Email = user.Email,
            Token = _tokenIssuer.CreateToken(user, expiresAt),
            ExpiresAt = expiresAt,
            IsAdmin = user.IsAdmin
        };
    }
}

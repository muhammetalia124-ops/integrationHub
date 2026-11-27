using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using IntegrationHub.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

namespace IntegrationHub.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    public const int GuestUserId = 0;

    private const string AppSecretKey = "AppSecret";
    private const string AppSecretHeader = "X-App-Secret";

    private readonly AuthService _authService;
    private readonly AccountSecurityService _accountSecurity;
    private readonly IConfiguration _configuration;
    private readonly ITokenIssuer _tokenIssuer;
    private readonly ITimeProvider _time;

    public AuthController(
        AuthService authService,
        AccountSecurityService accountSecurity,
        IConfiguration configuration,
        ITokenIssuer tokenIssuer,
        ITimeProvider time)
    {
        _authService = authService;
        _accountSecurity = accountSecurity;
        _configuration = configuration;
        _tokenIssuer = tokenIssuer;
        _time = time;
    }

    [HttpPost("register")]
    public async Task<ActionResult<RegisterResponse>> Register([FromBody] RegisterRequest req, CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(new ApiErrorResponse { Error = "Geçersiz istek.", Detail = "Lütfen tüm alanları kurallara uygun doldurun." });
        }

        var result = await _authService.RegisterAsync(req, ct);
        return result.Status == AuthOperationStatus.Conflict
            ? Conflict(new ApiErrorResponse { Error = result.Error! })
            : Ok(result.Response);
    }

    [HttpPost("verify-email")]
    public async Task<IActionResult> VerifyEmail([FromBody] VerifyEmailRequest req, CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(new ApiErrorResponse { Error = "Geçersiz istek.", Detail = "Geçerli bir e-posta adresi girin." });
        }

        var result = await _accountSecurity.VerifyEmailAsync(req, ct);
        return result.Status == AuthOperationStatus.Success
            ? Ok(new { message = "E-posta adresiniz doğrulandı. Artık giriş yapabilirsiniz." })
            : BadRequest(new ApiErrorResponse { Error = result.Error! });
    }

    [HttpPost("resend-verification")]
    public async Task<IActionResult> ResendVerification([FromBody] ResendVerificationRequest req, CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(new ApiErrorResponse { Error = "Geçerli bir e-posta adresi girin." });
        }

        var result = await _accountSecurity.ResendVerificationAsync(req.Email, ct);
        return Ok(new
        {
            message = "Doğrulama e-postası gönderildi. Gelen kutunuzu kontrol edin.",
            devVerificationCode = IsDevelopment() ? result.DevelopmentCode : null
        });
    }

    [HttpPost("forgot-password")]
    public async Task<IActionResult> ForgotPassword([FromBody] ForgotPasswordRequest req, CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(new ApiErrorResponse { Error = "Geçerli bir e-posta adresi girin." });
        }

        await _accountSecurity.SendPasswordResetAsync(req.Email, ct);
        return Ok(new { message = AccountSecurityService.GenericResetMessage });
    }

    [HttpPost("reset-password")]
    public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordRequest req, CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(new ApiErrorResponse
            {
                Error = "Geçersiz istek.",
                Detail = "Geçerli bir e-posta ve en az 6 karakterlik yeni şifre girin."
            });
        }

        if (string.IsNullOrWhiteSpace(req.Code) && string.IsNullOrWhiteSpace(req.Token))
        {
            return BadRequest(new ApiErrorResponse { Error = "E-postadaki bağlantıyı kullanın veya doğrulama kodunu girin." });
        }

        var result = await _accountSecurity.ResetPasswordAsync(req, ct);
        return result.Status == AuthOperationStatus.Success
            ? Ok(new { message = "Şifreniz güncellendi. Yeni şifrenizle giriş yapabilirsiniz." })
            : BadRequest(new ApiErrorResponse { Error = result.Error! });
    }

    [HttpPost("login")]
    public async Task<ActionResult<AuthResponse>> Login([FromBody] LoginRequest req, CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(new ApiErrorResponse { Error = "Geçersiz istek." });
        }

        var result = await _authService.LoginAsync(req, ct);
        return result.Status switch
        {
            AuthOperationStatus.Locked => StatusCode(StatusCodes.Status429TooManyRequests,
                new ApiErrorResponse { Error = result.Error! }),
            AuthOperationStatus.EmailNotVerified => StatusCode(StatusCodes.Status403Forbidden,
                new ApiErrorResponse { Error = result.Error!, Detail = "email-not-verified" }),
            AuthOperationStatus.Unauthorized => Unauthorized(new ApiErrorResponse
            {
                Error = result.Error!,
                Detail = result.RemainingAttempts?.ToString()
            }),
            _ => Ok(result.Response)
        };
    }

    [AllowAnonymous]
    [HttpPost("refresh")]
    public async Task<ActionResult<AuthResponse>> Refresh([FromBody] RefreshTokenRequest req, CancellationToken ct)
    {
        var result = await _authService.RefreshAsync(req.RefreshToken, ct);
        return result.Status == AuthOperationStatus.Success
            ? Ok(result.Response)
            : Unauthorized(new ApiErrorResponse { Error = result.Error! });
    }

    [AllowAnonymous]
    [HttpPost("logout")]
    public async Task<IActionResult> Logout([FromBody] LogoutRequest? req, CancellationToken ct)
    {
        await _authService.LogoutAsync(req?.RefreshToken, ct);
        return NoContent();
    }

    [AllowAnonymous]
    [HttpPost("guest")]
    public ActionResult<AuthResponse> Guest()
    {
        var expiresAt = _time.UtcNow.AddMinutes(60);
        var guest = new User
        {
            Id = GuestUserId,
            Email = "misafir@local",
            Username = "Misafir",
            IsActive = true,
            IsEmailVerified = false
        };

        return Ok(new AuthResponse
        {
            UserId = GuestUserId,
            Username = "Misafir",
            Email = string.Empty,
            Token = _tokenIssuer.CreateToken(guest, expiresAt),
            ExpiresAt = expiresAt,
            IsAdmin = false,
            IsEmailVerified = false,
            CanChangeUsername = false,
            UsernameChangeIntervalDays = 0,
            IsGuest = true
        });
    }

    [Authorize]
    [HttpGet("me")]
    public async Task<ActionResult<AuthResponse>> Me(CancellationToken ct)
    {
        if (!TryGetUserId(out var userId))
        {
            return Unauthorized(new ApiErrorResponse { Error = "Kimlik doğrulanamadı." });
        }

        if (userId == GuestUserId)
        {
            return Ok(BuildGuestIdentity());
        }

        var result = await _authService.GetCurrentUserAsync(userId, ct);
        return result.Status == AuthOperationStatus.Unauthorized
            ? Unauthorized(new ApiErrorResponse { Error = result.Error! })
            : Ok(result.Response);
    }

    [Authorize]
    [HttpPost("change-password")]
    public async Task<ActionResult<AuthResponse>> ChangePassword(
        [FromBody] ChangePasswordRequest req,
        CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(new ApiErrorResponse { Error = "Geçersiz istek.", Detail = "Yeni şifre en az 6 karakter olmalıdır." });
        }

        if (!TryGetUserId(out var userId))
        {
            return Unauthorized(new ApiErrorResponse { Error = "Kimlik doğrulanamadı." });
        }

        if (userId == GuestUserId)
        {
            return GuestNotAllowed();
        }

        var result = await _authService.ChangePasswordAsync(userId, req, ct);
        return result.Status switch
        {
            AuthOperationStatus.Unauthorized => Unauthorized(new ApiErrorResponse { Error = result.Error! }),
            AuthOperationStatus.Invalid => BadRequest(new ApiErrorResponse { Error = result.Error! }),
            _ => Ok(result.Response)
        };
    }

    [Authorize]
    [HttpPost("change-username")]
    public async Task<ActionResult<AuthResponse>> ChangeUsername(
        [FromBody] ChangeUsernameRequest req,
        CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(new ApiErrorResponse { Error = "Geçersiz istek.", Detail = "Kullanıcı adı en az 3 karakter olmalıdır." });
        }

        if (!TryGetUserId(out var userId))
        {
            return Unauthorized(new ApiErrorResponse { Error = "Kimlik doğrulanamadı." });
        }

        if (userId == GuestUserId)
        {
            return GuestNotAllowed();
        }

        var result = await _authService.ChangeUsernameAsync(userId, req, ct);
        return result.Status switch
        {
            AuthOperationStatus.Unauthorized => Unauthorized(new ApiErrorResponse { Error = result.Error! }),
            AuthOperationStatus.Invalid => BadRequest(new ApiErrorResponse { Error = result.Error! }),
            _ => Ok(result.Response)
        };
    }

    [Authorize]
    [HttpPost("delete-account")]
    public async Task<IActionResult> DeleteAccount(
        [FromBody] DeleteAccountRequest req,
        CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(new ApiErrorResponse { Error = "Geçersiz istek.", Detail = "Hesabınızı silmek için şifrenizi girin." });
        }

        if (!TryGetUserId(out var userId))
        {
            return Unauthorized(new ApiErrorResponse { Error = "Kimlik doğrulanamadı." });
        }

        if (userId == GuestUserId)
        {
            return GuestNotAllowed();
        }

        var result = await _authService.DeleteAccountAsync(userId, req, ct);
        return result.Status switch
        {
            AuthOperationStatus.Unauthorized => Unauthorized(new ApiErrorResponse { Error = result.Error! }),
            AuthOperationStatus.Conflict => Conflict(new ApiErrorResponse { Error = result.Error! }),
            AuthOperationStatus.Invalid => BadRequest(new ApiErrorResponse { Error = result.Error! }),
            _ => Ok(new { message = "Hesabınız ve geçmiş işlemleriniz silindi." })
        };
    }

    [AllowAnonymous]
    [HttpPost("auto-login")]
    public async Task<ActionResult<AuthResponse>> AutoLogin([FromBody] AutoLoginRequest req, CancellationToken ct)
    {
        if (!IsAppSecretValid(Request.Headers[AppSecretHeader].ToString()))
        {
            return Unauthorized(new ApiErrorResponse { Error = "Uygulama anahtarı geçersiz." });
        }

        if (!ModelState.IsValid)
        {
            return BadRequest(new ApiErrorResponse { Error = "Geçersiz istek.", Detail = "Email, kullanıcı adı ve şifre zorunludur." });
        }

        var result = await _authService.AutoLoginAsync(req, ct);
        return result.Status == AuthOperationStatus.Unauthorized
            ? Unauthorized(new ApiErrorResponse { Error = result.Error! })
            : Ok(result.Response);
    }

    private AuthResponse BuildGuestIdentity() => new()
    {
        UserId = GuestUserId,
        Username = "Misafir",
        Email = string.Empty,
        IsAdmin = false,
        IsEmailVerified = false,
        CanChangeUsername = false,
        UsernameChangeIntervalDays = 0,
        IsGuest = true
    };

    private ObjectResult GuestNotAllowed() =>
        new(new ApiErrorResponse { Error = "Bu işlem için kayıt olmanız gerekiyor." })
        {
            StatusCode = StatusCodes.Status403Forbidden
        };

    private bool IsDevelopment() =>
        string.Equals(_configuration["ASPNETCORE_ENVIRONMENT"], "Development", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(_configuration["DOTNET_ENVIRONMENT"], "Development", StringComparison.OrdinalIgnoreCase);

    private bool IsAppSecretValid(string providedSecret)
    {
        var expectedSecret = _configuration[AppSecretKey];
        if (string.IsNullOrWhiteSpace(expectedSecret) || string.IsNullOrWhiteSpace(providedSecret))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(providedSecret),
            Encoding.UTF8.GetBytes(expectedSecret));
    }

    private bool TryGetUserId(out int userId)
    {
        if (HttpContext.Items.TryGetValue("UserId", out var idObj) && idObj is int id)
        {
            userId = id;
            return true;
        }

        var claim = User.FindFirst(ClaimTypes.NameIdentifier) ?? User.FindFirst(JwtRegisteredClaimNames.Sub) ?? User.FindFirst("sub");
        userId = 0;
        return claim is not null && int.TryParse(claim.Value, out userId);
    }
}

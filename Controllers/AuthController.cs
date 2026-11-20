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
    private const string AppSecretKey = "AppSecret";
    private const string AppSecretHeader = "X-App-Secret";

    private readonly AuthService _authService;
    private readonly IConfiguration _configuration;

    public AuthController(AuthService authService, IConfiguration configuration)
    {
        _authService = authService;
        _configuration = configuration;
    }

    [HttpPost("register")]
    public async Task<ActionResult<AuthResponse>> Register([FromBody] RegisterRequest req, CancellationToken ct)
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

    [HttpPost("login")]
    public async Task<ActionResult<AuthResponse>> Login([FromBody] LoginRequest req, CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(new ApiErrorResponse { Error = "Geçersiz istek." });
        }

        var result = await _authService.LoginAsync(req, ct);
        return result.Status == AuthOperationStatus.Unauthorized
            ? Unauthorized(new ApiErrorResponse { Error = result.Error! })
            : Ok(result.Response);
    }

    [Authorize]
    [HttpGet("me")]
    public async Task<ActionResult<AuthResponse>> Me(CancellationToken ct)
    {
        if (!TryGetUserId(out var userId))
        {
            return Unauthorized(new ApiErrorResponse { Error = "Kimlik doğrulanamadı." });
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

        var result = await _authService.ChangePasswordAsync(userId, req, ct);
        return result.Status == AuthOperationStatus.Unauthorized
            ? Unauthorized(new ApiErrorResponse { Error = result.Error! })
            : Ok(result.Response);
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

        var claim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier) ?? User.FindFirst("sub");
        userId = 0;
        return claim is not null && int.TryParse(claim.Value, out userId);
    }
}

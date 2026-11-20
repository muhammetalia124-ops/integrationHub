using IntegrationHub.Domain;
using System.IdentityModel.Tokens.Jwt;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace IntegrationHub.Middlewares;

public class JwtMiddleware
{
    private static readonly string[] PublicApiPaths = { "/api/auth/login", "/api/auth/register", "/api/auth/auto-login" };

    private readonly RequestDelegate _next;
    private readonly TokenValidationParameters _validationParameters;
    private readonly ILogger<JwtMiddleware> _logger;

    public JwtMiddleware(RequestDelegate next, IOptions<JwtSettings> settings, ILogger<JwtMiddleware> logger)
    {
        _next = next;
        _logger = logger;
        var value = settings.Value;
        _validationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = value.Issuer,
            ValidateAudience = true,
            ValidAudience = value.Audience,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(value.SecretKey)),
            ClockSkew = TimeSpan.FromSeconds(30)
        };
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (IsPublicPath(context.Request.Path.Value ?? string.Empty))
        {
            await _next(context);
            return;
        }

        var authHeader = context.Request.Headers.Authorization.ToString();
        if (!authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            await WriteUnauthorizedAsync(context, "Token bulunamadı.");
            return;
        }

        try
        {
            var principal = new JwtSecurityTokenHandler().ValidateToken(
                authHeader["Bearer ".Length..].Trim(),
                _validationParameters,
                out _);

            var userIdClaim = principal.FindFirst(JwtRegisteredClaimNames.Sub)
                              ?? principal.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier);

            if (userIdClaim is null || !int.TryParse(userIdClaim.Value, out var userId))
            {
                await WriteUnauthorizedAsync(context, "Geçersiz token içeriği.");
                return;
            }

            context.Items["UserId"] = userId;
            context.User = principal;
            await _next(context);
        }
        catch (SecurityTokenException ex)
        {
            _logger.LogWarning("JWT doğrulama hatası: {Message}", ex.Message);
            await WriteUnauthorizedAsync(context, "Geçersiz veya süresi dolmuş token.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "JWT işlenirken beklenmeyen hata.");
            await WriteUnauthorizedAsync(context, "Kimlik doğrulama hatası.");
        }
    }

    private static bool IsPublicPath(string path) =>
        !path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) ||
        PublicApiPaths.Contains(path, StringComparer.OrdinalIgnoreCase);

    private static async Task WriteUnauthorizedAsync(HttpContext context, string message)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsJsonAsync(new { error = message });
    }
}

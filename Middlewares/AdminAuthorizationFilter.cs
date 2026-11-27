using IntegrationHub.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace IntegrationHub.Middlewares;

public sealed class AdminAuthorizationFilter : IAsyncAuthorizationFilter
{
    private readonly AuthService _auth;

    public AdminAuthorizationFilter(AuthService auth)
    {
        _auth = auth;
    }

    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        if (!context.HttpContext.Items.TryGetValue("UserId", out var value) || value is not int userId)
        {
            context.Result = new UnauthorizedObjectResult(new ApiErrorResponse { Error = "Kimlik doğrulanamadı." });
            return;
        }

        var isAdmin = await _auth.IsActiveAdminAsync(userId, context.HttpContext.RequestAborted);

        if (!isAdmin)
        {
            context.Result = new ObjectResult(new ApiErrorResponse { Error = "Bu işlem için yönetici yetkisi gerekiyor." })
            {
                StatusCode = StatusCodes.Status403Forbidden
            };
        }
    }
}

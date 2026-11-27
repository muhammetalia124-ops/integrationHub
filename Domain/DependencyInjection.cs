using Microsoft.Extensions.DependencyInjection;

namespace IntegrationHub.Domain;

public static class DependencyInjection
{
    public static IServiceCollection AddDomain(this IServiceCollection services)
    {
        services.AddMemoryCache();
        services.AddSingleton<LoginThrottle>();
        services.AddSingleton<ITimeProvider, SystemTimeProvider>();
        services.AddSingleton<AppEnvironmentInfo>(sp => new AppEnvironmentInfo
        {
            Name = sp.GetService<Microsoft.Extensions.Hosting.IHostEnvironment>()?.EnvironmentName ?? "Production"
        });
        services.AddScoped<AuthService>();
        services.AddScoped<AccountSecurityService>();
        services.AddScoped<AdminService>();
        services.AddScoped<UserActivityService>();
        services.AddScoped<SearchService>();
        services.AddScoped<SharedApiDataService>();
        return services;
    }
}

public sealed class SystemTimeProvider : ITimeProvider
{
    public DateTime UtcNow => DateTime.UtcNow;
}

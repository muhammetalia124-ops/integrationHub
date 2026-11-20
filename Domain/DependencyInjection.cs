using Microsoft.Extensions.DependencyInjection;

namespace IntegrationHub.Domain;

public static class DependencyInjection
{
    public static IServiceCollection AddDomain(this IServiceCollection services)
    {
        services.AddScoped<AuthService>();
        services.AddScoped<AdminService>();
        services.AddScoped<UserActivityService>();
        services.AddScoped<SearchService>();
        services.AddScoped<SharedApiDataService>();
        return services;
    }
}

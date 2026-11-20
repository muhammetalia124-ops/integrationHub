using System.Reflection;
using IntegrationHub.Domain;
using IntegrationHub.Integrations;
using IntegrationHub.Infrastructure.Persistence;
using IntegrationHub.Infrastructure.Persistence.Repositories;
using IntegrationHub.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace IntegrationHub.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddPersistence(configuration);

        services.AddDomain();

        services.AddHttpClient<CurrencyRateProvider>(client => ConfigureClient(client, TimeSpan.FromSeconds(20)));
        services.AddScoped<IExchangeRateProvider>(sp => sp.GetRequiredService<CurrencyRateProvider>());
        services.AddHttpClient<ILocationLookupService, LocationLookupService>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(10);
            client.BaseAddress = new Uri("https://nominatim.openstreetmap.org/");
            client.DefaultRequestHeaders.UserAgent.ParseAdd("IntegrationHub/1.0 (https://github.com)");
            client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("tr");
        });
        services.AddIntegrationServices();

        services.AddScoped<IPasswordHasher, PasswordHasher>();
        services.AddSingleton<ITokenIssuer, JwtTokenIssuer>();

        return services;
    }

    public static IServiceCollection AddPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<UserDbContext>(options =>
            options.UseNpgsql(configuration.GetConnectionString("UserDbConnection")));
        services.AddDbContext<UserActivityDbContext>(options =>
            options.UseNpgsql(configuration.GetConnectionString("ActivityDbConnection")));
        services.AddDbContext<CurrencyRateDbContext>(options =>
            options.UseNpgsql(configuration.GetConnectionString("CurrencyRatesConnection")));

        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IUserActivityRepository, UserActivityRepository>();

        services.AddScoped<SharedCacheStore>();
        services.AddScoped<IExternalApiCache>(sp => sp.GetRequiredService<SharedCacheStore>());
        services.AddScoped<ISharedApiDataStore>(sp => sp.GetRequiredService<SharedCacheStore>());
        services.AddScoped<ICurrencyCacheStore>(sp => sp.GetRequiredService<SharedCacheStore>());
        services.AddScoped<IPrayerTimeCacheStore>(sp => sp.GetRequiredService<SharedCacheStore>());
        services.AddScoped<IDatabaseInitializer, DatabaseInitializer>();
        return services;
    }

    private static IServiceCollection AddIntegrationServices(this IServiceCollection services)
    {
        var contract = typeof(IIntegrationService);
        var implementations = Assembly.GetExecutingAssembly()
            .GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false } && contract.IsAssignableFrom(type));

        foreach (var implementation in implementations)
        {
            var clientName = implementation.FullName ?? implementation.Name;
            services.AddHttpClient(clientName, client => ConfigureClient(client, TimeSpan.FromSeconds(20)));

            services.AddTransient(implementation, sp => ActivatorUtilities.CreateInstance(
                sp,
                implementation,
                sp.GetRequiredService<IHttpClientFactory>().CreateClient(clientName)));
            services.AddTransient(contract, sp => (IIntegrationService)sp.GetRequiredService(implementation));
        }

        return services;
    }

    private static void ConfigureClient(HttpClient client, TimeSpan timeout)
    {
        client.Timeout = timeout;
        client.DefaultRequestHeaders.UserAgent.ParseAdd("IntegrationHub/1.0");
    }
}

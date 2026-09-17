using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ESbirka.Client;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddESbirkaClient(this IServiceCollection services, Action<ESbirkaClientOptions>? configure = null)
    {
        var options = new ESbirkaClientOptions();
        configure?.Invoke(options);
        options.Validate();
        services.AddSingleton(options);
        services.TryAddSingleton<IESbirkaCache, SqliteESbirkaCache>();
        services.TryAddSingleton<ESbirkaRequestCoordinator>();
        services.AddScoped<ESbirkaClient>();
        services.AddScoped<IESbirkaClient>(provider => provider.GetRequiredService<ESbirkaClient>());
        services.AddScoped<IESbirkaCacheAdministration>(provider => provider.GetRequiredService<ESbirkaClient>());
        return services;
    }
}
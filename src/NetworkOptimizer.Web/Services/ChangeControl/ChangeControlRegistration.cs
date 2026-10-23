using NetworkOptimizer.Storage.Interfaces;
using NetworkOptimizer.Storage.Repositories;
using NetworkOptimizer.Storage.Services;

namespace NetworkOptimizer.Web.Services.ChangeControl;

/// <summary>Registers Change Control's services.</summary>
public static class ChangeControlRegistration
{
    public static IServiceCollection AddChangeControl(this IServiceCollection services)
    {
        services.AddScoped<IChangeControlRepository, ChangeControlRepository>();
        services.AddSingleton<IConfigBlobStore, ConfigBlobStore>();
        services.AddSiteScopedRegistry<ChangeControlRegistry>();
        services.AddHostedService(sp => sp.GetRequiredService<ChangeControlRegistry>());
        return services;
    }
}

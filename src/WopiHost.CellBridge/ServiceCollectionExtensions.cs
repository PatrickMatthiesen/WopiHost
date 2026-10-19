using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using CellBridge.AspNetCore;
using WopiHost.Abstractions;

namespace WopiHost.CellBridge;

/// <summary>
/// Registration for the cellbridge-backed MS-FSSHTTP processor.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the opt-in shared-authority adapter. First configure AddCellBridge with a
    /// host-owned policy. TRevisionStore must implement real conditional writes and durable receipts.
    /// Whole-file saves use that authority. Namespace mutation routes remain disabled.
    /// </summary>
    public static IServiceCollection AddCoordinatedCellBridgeProcessor<TRevisionStore>(
        this IServiceCollection services, IConfiguration configuration)
        where TRevisionStore : class, IWopiRevisionStore
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        if (services.Any(d => d.ServiceType == typeof(ICobaltProcessor) || d.ServiceType == typeof(IWopiEditingCoordinator)))
            throw new InvalidOperationException("Configure exactly one editing backend and shared authority.");
        if (services.Any(d => d.ServiceType == typeof(TRevisionStore) && d.Lifetime != ServiceLifetime.Singleton))
            throw new InvalidOperationException("The conditional destination must be a singleton.");
        services.AddOptions<CoordinatedCellBridgeOptions>()
            .Bind(configuration.GetSection("Wopi:CellBridge:Coordinated"))
            .Validate(options => { options.Validate(); return true; })
            .ValidateOnStart();
        services.TryAddSingleton<TRevisionStore>();
        services.AddSingleton<IWopiRevisionStore>(sp => sp.GetRequiredService<TRevisionStore>());
        services.AddCellBridgeExternalPublishing<WopiRevisionDestination>();
        services.AddSingleton<CoordinatedCellBridgeProcessor>();
        services.AddSingleton<ICobaltProcessor>(sp => sp.GetRequiredService<CoordinatedCellBridgeProcessor>());
        services.AddSingleton<IWopiEditingCoordinator>(sp => sp.GetRequiredService<CoordinatedCellBridgeProcessor>());
#if CELLBRIDGE_WHOLE_FILE_SAVES
        services.AddSingleton<IWopiCoordinatedFileMutations>(sp => sp.GetRequiredService<CoordinatedCellBridgeProcessor>());
#endif
        return services;
    }

    /// <summary>
    /// Registers <see cref="CellBridgeProcessor"/> as the host's <see cref="ICobaltProcessor"/>, binding
    /// <see cref="CellBridgeProcessorOptions"/> from <c>Wopi:CellBridge</c>. Exactly one MS-FSSHTTP
    /// backend can be active per host, so a second registration is a configuration error.
    /// </summary>
    public static IServiceCollection AddCellBridgeProcessor(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        if (services.Any(d => d.ServiceType == typeof(ICobaltProcessor)))
        {
            throw new InvalidOperationException(
                $"An {nameof(ICobaltProcessor)} is already registered. Exactly one MS-FSSHTTP backend (Microsoft.CobaltCore or cellbridge) can be active per host.");
        }

        services.AddOptions<CellBridgeProcessorOptions>()
            .Bind(configuration.GetSection(CellBridgeProcessorOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<ICobaltProcessor, CellBridgeProcessor>();
        return services;
    }
}

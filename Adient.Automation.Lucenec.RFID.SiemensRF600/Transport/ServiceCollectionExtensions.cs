using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Adient.Automation.Lucenec.RFID.SiemensRF600.Transport;

/// <summary>
/// Extension methods for dependency injection
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Add RFID transport service to the service collection
    /// </summary>
    public static IServiceCollection AddRfidTransportService(this IServiceCollection services)
    {
        services.TryAddTransient<IRfidTransportService, RfidTcpTransportService>();
        return services;
    }

    /// <summary>
    /// Add RFID transport service as singleton
    /// </summary>
    public static IServiceCollection AddRfidTransportServiceSingleton(this IServiceCollection services)
    {
        services.TryAddSingleton<IRfidTransportService, RfidTcpTransportService>();
        return services;
    }

    /// <summary>
    /// Add RFID transport service with configuration
    /// </summary>
    public static IServiceCollection AddRfidTransportService(
        this IServiceCollection services,
        Action<TransportConfiguration> configureOptions)
    {
        var configuration = new TransportConfiguration { Host = string.Empty };
        configureOptions(configuration);
        configuration.Validate();

        services.TryAddSingleton(configuration);
        services.TryAddTransient<IRfidTransportService, RfidTcpTransportService>();

        return services;
    }
}
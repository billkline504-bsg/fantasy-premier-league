using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace EplFantasy.Infrastructure;

public static class DeadlineSweepServiceCollectionExtensions
{
    /// <summary>
    /// Registers the ADR-012 sweep loop against the "DeadlineSweep" configuration section (an
    /// absent section is fine — <see cref="DeadlineSweepOptions"/>'s default Interval applies).
    /// Registers no <c>IDeadlineSweepHandler</c> of its own — a feature task adds one via
    /// <c>services.AddScoped&lt;IDeadlineSweepHandler, YourHandler&gt;()</c> as it builds the
    /// domain logic that handler owns; this call only needs to run once regardless of how many
    /// handlers exist.
    /// </summary>
    public static IServiceCollection AddDeadlineSweep(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<DeadlineSweepOptions>(configuration.GetSection(DeadlineSweepOptions.SectionName));
        services.AddHostedService<DeadlineSweepBackgroundService>();

        return services;
    }
}

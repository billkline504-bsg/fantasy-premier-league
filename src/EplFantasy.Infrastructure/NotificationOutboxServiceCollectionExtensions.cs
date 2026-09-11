using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace EplFantasy.Infrastructure;

public static class NotificationOutboxServiceCollectionExtensions
{
    /// <summary>
    /// Registers the outbox-draining background service against the "NotificationOutbox"
    /// configuration section (an absent section is fine — NotificationOutboxOptions' defaults
    /// apply). Registers no <c>INotificationSender</c> of its own — the real email/SMS provider
    /// is still an open decision (Architecture §15); F-012.4 adds one via
    /// <c>services.AddSingleton&lt;INotificationSender, YourSender&gt;()</c> once it's made.
    /// </summary>
    public static IServiceCollection AddNotificationOutbox(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<NotificationOutboxOptions>(configuration.GetSection(NotificationOutboxOptions.SectionName));
        services.AddHostedService<NotificationOutboxBackgroundService>();

        return services;
    }
}

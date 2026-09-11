using EplFantasy.Administration;
using EplFantasy.SharedKernel;

namespace EplFantasy.Infrastructure;

/// <summary>Registered Scoped (see ServiceCollectionExtensions.AddInfrastructure) — see the interface's own remarks on why this commits itself rather than staging for a caller's SaveChangesAsync().</summary>
public sealed class SecurityEventRecorder(EplFantasyDbContext dbContext, IClock clock) : ISecurityEventRecorder
{
    public async Task RecordAsync(
        SecurityEventType eventType,
        string endpoint,
        string scope,
        string detail,
        CancellationToken cancellationToken = default)
    {
        dbContext.SecurityEvents.Add(new SecurityEvent
        {
            SecurityEventId = Guid.NewGuid(),
            EventType = eventType,
            Endpoint = endpoint,
            Scope = scope,
            Detail = detail,
            OccurredAt = clock.UtcNow,
        });

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}

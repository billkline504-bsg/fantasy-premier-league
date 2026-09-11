using System.Text.Json;
using EplFantasy.Administration;
using EplFantasy.SharedKernel;

namespace EplFantasy.Infrastructure;

/// <summary>
/// Registered Scoped (see ServiceCollectionExtensions.AddInfrastructure), the same lifetime as
/// <see cref="EplFantasyDbContext"/> — within one request/unit-of-work, this and any application
/// service resolve the *same* DbContext instance, which is what makes <see cref="Record"/>'s
/// staged row commit atomically with whatever domain change the caller's own SaveChangesAsync()
/// persists, without either side needing to coordinate a shared transaction explicitly.
/// </summary>
public sealed class AdministrativeActionRecorder(EplFantasyDbContext dbContext, IClock clock) : IAdministrativeActionRecorder
{
    public void Record(
        Guid leagueId,
        Guid? actingMembershipId,
        AdminActionType actionType,
        string targetEntityType,
        Guid targetEntityId,
        object beforeState,
        object afterState,
        string? reason = null)
    {
        dbContext.AdministrativeActions.Add(new AdministrativeAction
        {
            ActionId = Guid.NewGuid(),
            LeagueId = leagueId,
            ActingMembershipId = actingMembershipId,
            ActionType = actionType,
            TargetEntityType = targetEntityType,
            TargetEntityId = targetEntityId,
            BeforeStateJson = JsonSerializer.Serialize(beforeState),
            AfterStateJson = JsonSerializer.Serialize(afterState),
            Reason = reason,
            CreatedAt = clock.UtcNow,
        });

        // Deliberately no SaveChangesAsync() call here — see this class's and the interface's own
        // remarks on why atomicity depends on the caller committing both changes in one call.
    }
}

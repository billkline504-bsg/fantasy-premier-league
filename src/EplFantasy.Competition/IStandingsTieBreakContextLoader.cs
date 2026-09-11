namespace EplFantasy.Competition;

/// <summary>Loads everything a Season's FantasyTeams need for tie-break comparisons, once, upfront — see IStandingsTieBreakRule's remarks on why.</summary>
public interface IStandingsTieBreakContextLoader
{
    Task<IStandingsTieBreakContext> LoadAsync(Guid seasonId, CancellationToken cancellationToken = default);
}

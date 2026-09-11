namespace EplFantasy.Leagues;

/// <summary>IT-51 (F-003.6, BR-221-BR-223): League Administrator-only publishing of League messages.</summary>
public interface ILeagueMessageService
{
    /// <summary>
    /// Resolves <paramref name="actingUserId"/>'s own active, Administrator LeagueMembership of
    /// <paramref name="leagueId"/> — the caller is always "whoever is asking," never a
    /// LeagueMembershipId the request itself supplies — and publishes the message under it.
    /// </summary>
    Task<LeagueMessage> PublishAsync(Guid leagueId, Guid actingUserId, string body, CancellationToken cancellationToken = default);
}

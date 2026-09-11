using EplFantasy.SharedKernel;

namespace EplFantasy.Leagues;

/// <summary>
/// F-003.2's invitation issue/revoke/accept orchestration (IT-04). Declared here, in the bounded
/// context that owns the Invitation and LeagueMembership aggregates; implemented in
/// EplFantasy.Infrastructure (it needs the real DbContext), matching every other
/// application-service seam in this codebase.
/// </summary>
public interface IInvitationService
{
    /// <summary>
    /// BR-027–BR-029: issues a new invitation with a fresh, unguessable token. Fails with
    /// <c>"season_not_found"</c> if <paramref name="seasonId"/> is supplied but does not name a
    /// real Season of this League.
    /// </summary>
    Task<Result<Invitation>> CreateInvitationAsync(
        Guid leagueId,
        string destination,
        InvitationChannel channel,
        Guid? seasonId,
        CancellationToken cancellationToken = default);

    /// <summary>Fails with <c>"invitation_not_found"</c> if invitationId does not name a real invitation of this League.</summary>
    Task<Result> RevokeInvitationAsync(Guid leagueId, Guid invitationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// BR-027: creates (or, per BR-020, re-creates after a prior "Left" row) the caller's
    /// LeagueMembership for the invited League. Fails with the single <c>"invitation_invalid"</c>
    /// code for every reason the token can't be redeemed right now — unknown, expired (BR-029),
    /// already accepted, or revoked — deliberately never distinguishing which, the same
    /// non-enumerable-failure shape IUserAccountService.AuthenticateAsync uses for
    /// "invalid_credentials".
    /// </summary>
    Task<Result<LeagueMembership>> AcceptInvitationAsync(string token, Guid userId, CancellationToken cancellationToken = default);
}

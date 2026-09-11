namespace EplFantasy.Identity;

/// <summary>
/// IT-57 (F-013.2, BR-014/BR-175/BR-272/BR-326): the one place any historical-display code path
/// resolves a User's display username, rather than reading <c>User.Username</c> (today's value)
/// directly. BR-326 requires a finalized historical record to keep showing the username the User
/// held at the time that record was created, permanently — even after later renames, and even
/// after the User retires (BR-014/BR-175/BR-272: a retired User's history keeps resolving
/// correctly, never breaking or disappearing). Declared here, in the bounded context that owns
/// <c>UsernameHistory</c>; implemented in EplFantasy.Infrastructure, matching every other
/// application-service seam in this codebase.
/// </summary>
public interface IUsernameHistoryResolver
{
    /// <summary>The username a single User held as of one instant.</summary>
    Task<string> ResolveAsOfAsync(Guid userId, DateTimeOffset asOf, CancellationToken cancellationToken = default);

    /// <summary>
    /// Batches <see cref="ResolveAsOfAsync"/> for many (User, instant) pairs at once — one
    /// UsernameHistory query total, resolved in memory — keyed by whatever caller-supplied
    /// <c>Key</c> each request carries (e.g. a FantasyTeamId), not by UserId itself, since one User
    /// can own more than one request (e.g. more than one FantasyTeam across Leagues).
    /// </summary>
    Task<Dictionary<Guid, string>> ResolveManyAsOfAsync(
        IReadOnlyList<(Guid Key, Guid UserId, DateTimeOffset AsOf)> requests, CancellationToken cancellationToken = default);
}

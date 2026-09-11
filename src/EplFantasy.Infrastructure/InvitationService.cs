using System.Security.Cryptography;
using EplFantasy.Leagues;
using EplFantasy.Notifications;
using EplFantasy.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace EplFantasy.Infrastructure;

/// <summary>Registered Scoped (see ServiceCollectionExtensions.AddInfrastructure) — the same lifetime as EplFantasyDbContext, so each method's writes commit in a single SaveChangesAsync().</summary>
public sealed class InvitationService(EplFantasyDbContext dbContext, IClock clock) : IInvitationService
{
    private static readonly Error SeasonNotFound = new("season_not_found", "No Season with that id exists in this League.");
    private static readonly Error InvitationNotFound = new("invitation_not_found", "No invitation with that id exists in this League.");
    private static readonly Error InvitationInvalid = new(
        "invitation_invalid",
        "This invitation is unknown, expired, already accepted, or revoked.");

    public async Task<Result<Invitation>> CreateInvitationAsync(
        Guid leagueId,
        string destination,
        InvitationChannel channel,
        Guid? seasonId,
        CancellationToken cancellationToken = default)
    {
        if (seasonId is not null
            && !await dbContext.Seasons.AnyAsync(s => s.SeasonId == seasonId && s.LeagueId == leagueId, cancellationToken))
        {
            return Result.Failure<Invitation>(SeasonNotFound);
        }

        // BR-292: every League has a LeagueConfiguration row from the moment it's created
        // (LeagueService.CreateAsync seeds one at League-creation time) — this reads the League's
        // *current* value, never a hard-coded "7 days" literal (AP-006).
        var configuration = await dbContext.LeagueConfigurations.SingleAsync(c => c.LeagueId == leagueId, cancellationToken);

        var now = clock.UtcNow;
        var invitation = Invitation.Issue(
            Guid.NewGuid(),
            leagueId,
            seasonId,
            GenerateToken(),
            destination,
            channel,
            configuration.InvitationExpirationDays,
            now);

        dbContext.Invitations.Add(invitation);
        await dbContext.SaveChangesAsync(cancellationToken);

        return Result.Success(invitation);
    }

    public async Task<Result> RevokeInvitationAsync(Guid leagueId, Guid invitationId, CancellationToken cancellationToken = default)
    {
        var invitation = await dbContext.Invitations.SingleOrDefaultAsync(
            i => i.InvitationId == invitationId && i.LeagueId == leagueId,
            cancellationToken);

        if (invitation is null)
        {
            return Result.Failure(InvitationNotFound);
        }

        invitation.Revoke();
        await dbContext.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }

    public async Task<Result<LeagueMembership>> AcceptInvitationAsync(string token, Guid userId, CancellationToken cancellationToken = default)
    {
        var now = clock.UtcNow;
        var invitation = await dbContext.Invitations.SingleOrDefaultAsync(i => i.Token == token, cancellationToken);

        if (invitation is null || !invitation.IsAcceptable(now))
        {
            return Result.Failure<LeagueMembership>(InvitationInvalid);
        }

        // BR-020: at most one non-"Left" membership per (League, User) — reuse it if the caller is
        // somehow already a member (idempotent accept); otherwise this is either a first-time join
        // or a rejoin after leaving, both a brand-new row (League.Join's own remarks).
        var existingMembership = await dbContext.LeagueMemberships.SingleOrDefaultAsync(
            m => m.LeagueId == invitation.LeagueId && m.UserId == userId && m.Status != MembershipStatus.Left,
            cancellationToken);

        var membership = existingMembership;
        if (membership is null)
        {
            membership = LeagueMembership.Join(Guid.NewGuid(), invitation.LeagueId, userId, now);
            dbContext.LeagueMemberships.Add(membership);
            // IT-53 (F-012.1, BR-338): a brand-new LeagueMembership row (first-time join or a
            // rejoin after leaving) gets its own full set of disabled-by-default preferences —
            // the reused-membership branch above needs no seeding, since its own row already has one.
            dbContext.NotificationPreferences.AddRange(NotificationPreferenceSeeder.SeedFor(membership.LeagueMembershipId));
        }

        invitation.Accept();

        await dbContext.SaveChangesAsync(cancellationToken);

        return Result.Success(membership);
    }

    // Plaintext, not hashed — unlike refresh_tokens/password_reset_tokens, an invitation token is
    // meant to be looked up directly by the public acceptInvitation endpoint (V004: `token text NOT
    // NULL`, no separate hash column) and isn't a session credential, so hashing it would add no
    // real protection while only complicating the lookup.
    private static string GenerateToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
}

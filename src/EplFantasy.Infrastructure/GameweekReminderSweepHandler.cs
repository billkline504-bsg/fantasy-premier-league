using System.Text.Json;
using EplFantasy.FantasyTeams;
using EplFantasy.Notifications;
using EplFantasy.PlayerData;
using EplFantasy.Rosters;
using EplFantasy.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EplFantasy.Infrastructure;

/// <summary>
/// IT-55 (F-012.2, BR-152/BR-338): ADR-012's shared sweep, applied to the moment a Gameweek's
/// roster deadline is *approaching* rather than to the deadline itself (that's IT-31's own
/// RosterLockSweepHandler). Every tick, for every (Season, Gameweek) pair whose reminder window
/// has opened — <c>now &gt;= RosterLockDeadline - SeasonConfiguration.GameweekReminderLeadTimeHours</c>
/// (BR-291/BR-309, a League/Season-configurable parameter defaulting to 24 hours) but whose
/// deadline hasn't passed yet — this queues a GameweekReminder NotificationRequest for every
/// Active FantasyTeam in that Season that has not yet submitted a valid roster for that Gameweek
/// (AC1/AC2). One request row is written per NotificationChannel value (Email and Sms), mirroring
/// NotificationPreferenceSeeder's own "one row per channel" shape (IT-53) — this handler never
/// reads NotificationPreference itself; it leaves the already-built
/// NotificationOutboxBackgroundService (IT-F12) to decide, per BR-226, whether that FantasyTeam's
/// own LeagueMembership actually wants it, entirely independently of any other League the same
/// User belongs to (BR-338).
///
/// Idempotency (a reminder must fire exactly once per FantasyTeam/Gameweek, not on every tick
/// until the deadline arrives) has no dedicated tracking column to lean on — V011's
/// notification_requests table is fixed for this task, no new migration — so it's derived from the
/// outbox itself: every already-queued GameweekReminder request for a FantasyTeam's
/// LeagueMembershipId is loaded and its PayloadJson's GameweekId compared before queuing another;
/// a match (whatever that request's own current Status — Pending, Sent, Suppressed, or Failed)
/// means this Gameweek was already queued and is skipped.
/// </summary>
public sealed class GameweekReminderSweepHandler(
    EplFantasyDbContext dbContext,
    IClock clock,
    ILogger<GameweekReminderSweepHandler> logger) : IDeadlineSweepHandler
{
    public string Name => "GameweekReminder";

    private sealed record GameweekReminderPayload(Guid GameweekId, int GameweekNumber, DateTimeOffset RosterLockDeadline);

    public async Task SweepAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;

        var openWindows = await (
            from seasonConfiguration in dbContext.SeasonConfigurations
            join season in dbContext.Seasons on seasonConfiguration.SeasonId equals season.SeasonId
            join gameweek in dbContext.Gameweeks on season.EplSeasonIdentifier equals gameweek.EplSeasonIdentifier
            where gameweek.RosterLockDeadline > now
               && gameweek.RosterLockDeadline <= now.AddHours(seasonConfiguration.GameweekReminderLeadTimeHours)
            select new { season.SeasonId, Gameweek = gameweek }
        ).ToListAsync(cancellationToken);

        if (openWindows.Count == 0)
        {
            return;
        }

        foreach (var window in openWindows)
        {
            await QueueRemindersForAsync(window.SeasonId, window.Gameweek, now, cancellationToken);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>AC1/AC2: every Active FantasyTeam in this Season without a Submitted roster for this Gameweek gets reminded, once, via its own LeagueMembership (BR-338).</summary>
    private async Task QueueRemindersForAsync(Guid seasonId, Gameweek gameweek, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var candidateTeams = await (
            from fantasyTeam in dbContext.FantasyTeams
            join membership in dbContext.LeagueMemberships on fantasyTeam.LeagueMembershipId equals membership.LeagueMembershipId
            where fantasyTeam.SeasonId == seasonId && fantasyTeam.Status == FantasyTeamStatus.Active
            select new { fantasyTeam.FantasyTeamId, fantasyTeam.LeagueMembershipId, membership.UserId }
        ).ToListAsync(cancellationToken);

        if (candidateTeams.Count == 0)
        {
            return;
        }

        var fantasyTeamIdsWithASubmittedRoster = await dbContext.GameweekRosters
            .Where(r => r.GameweekId == gameweek.GameweekId && r.Status == RosterStatus.Submitted)
            .Select(r => r.FantasyTeamId)
            .ToListAsync(cancellationToken);

        var unsubmittedTeams = candidateTeams.Where(t => !fantasyTeamIdsWithASubmittedRoster.Contains(t.FantasyTeamId)).ToList();
        if (unsubmittedTeams.Count == 0)
        {
            return;
        }

        var membershipIds = unsubmittedTeams.Select(t => t.LeagueMembershipId).ToList();
        var existingReminders = await dbContext.NotificationRequests
            .Where(r => r.EventType == NotificationEventType.GameweekReminder && membershipIds.Contains(r.LeagueMembershipId))
            .ToListAsync(cancellationToken);

        foreach (var team in unsubmittedTeams)
        {
            var alreadyQueuedForThisGameweek = existingReminders
                .Where(r => r.LeagueMembershipId == team.LeagueMembershipId)
                .Any(r => TryGetGameweekId(r.PayloadJson) == gameweek.GameweekId);

            if (alreadyQueuedForThisGameweek)
            {
                continue;
            }

            var payloadJson = JsonSerializer.Serialize(new GameweekReminderPayload(gameweek.GameweekId, gameweek.Number, gameweek.RosterLockDeadline));

            foreach (var channel in Enum.GetValues<NotificationChannel>())
            {
                dbContext.NotificationRequests.Add(new NotificationRequest
                {
                    RequestId = Guid.NewGuid(),
                    UserId = team.UserId,
                    LeagueMembershipId = team.LeagueMembershipId,
                    EventType = NotificationEventType.GameweekReminder,
                    Channel = channel,
                    PayloadJson = payloadJson,
                    Status = NotificationStatus.Pending,
                    CreatedAt = now,
                });
            }

            logger.LogInformation(
                "{Event}: GameweekReminder queued for LeagueMembership {LeagueMembershipId}, Gameweek {GameweekId}",
                "GameweekReminderQueued", team.LeagueMembershipId, gameweek.GameweekId);
        }
    }

    private static Guid? TryGetGameweekId(string payloadJson)
    {
        try
        {
            return JsonSerializer.Deserialize<GameweekReminderPayload>(payloadJson)?.GameweekId;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

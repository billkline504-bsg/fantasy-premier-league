using EplFantasy.Leagues;
using EplFantasy.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace EplFantasy.Infrastructure;

public sealed class LeagueMessageService(EplFantasyDbContext dbContext, IClock clock) : ILeagueMessageService
{
    public async Task<LeagueMessage> PublishAsync(Guid leagueId, Guid actingUserId, string body, CancellationToken cancellationToken = default)
    {
        var actingMembership = await dbContext.LeagueMemberships.SingleAsync(
            m => m.LeagueId == leagueId && m.UserId == actingUserId && m.IsAdministrator && m.Status == MembershipStatus.Active,
            cancellationToken);

        var message = LeagueMessage.Publish(Guid.NewGuid(), leagueId, actingMembership.LeagueMembershipId, body, clock.UtcNow);
        dbContext.LeagueMessages.Add(message);

        await dbContext.SaveChangesAsync(cancellationToken);

        return message;
    }
}

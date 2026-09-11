using EplFantasy.FantasyTeams;
using EplFantasy.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace EplFantasy.Infrastructure;

/// <summary>Registered Scoped (see ServiceCollectionExtensions.AddInfrastructure) — the same lifetime as EplFantasyDbContext, so CreateAsync's write commits in a single SaveChangesAsync().</summary>
public sealed class FantasyTeamService(EplFantasyDbContext dbContext, IClock clock) : IFantasyTeamService
{
    private static readonly Error FantasyTeamAlreadyExists = new(
        "fantasy_team_already_exists",
        "A FantasyTeam already exists for this LeagueMembership and Season.");

    public async Task<Result<FantasyTeam>> CreateAsync(Guid leagueMembershipId, Guid seasonId, CancellationToken cancellationToken = default)
    {
        // BR-193/Invariant 2's app-level pre-check — the actual correctness guarantee against a
        // concurrent create racing this same check is ux_fantasy_teams_membership_season (V005),
        // handled below via DbUpdateException, the same two-layer pattern
        // UserAccountService.RegisterAsync already established for username/email uniqueness.
        if (await dbContext.FantasyTeams.AnyAsync(
            t => t.LeagueMembershipId == leagueMembershipId && t.SeasonId == seasonId,
            cancellationToken))
        {
            return Result.Failure<FantasyTeam>(FantasyTeamAlreadyExists);
        }

        var fantasyTeam = FantasyTeam.Create(Guid.NewGuid(), leagueMembershipId, seasonId, clock.UtcNow);
        dbContext.FantasyTeams.Add(fantasyTeam);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            return Result.Failure<FantasyTeam>(FantasyTeamAlreadyExists);
        }

        return Result.Success(fantasyTeam);
    }
}

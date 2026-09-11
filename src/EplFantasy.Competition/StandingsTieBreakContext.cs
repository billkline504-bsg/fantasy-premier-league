namespace EplFantasy.Competition;

public sealed class StandingsTieBreakContext(
    Guid seasonId,
    IReadOnlyDictionary<Guid, SeasonGoalPrediction> predictionsByFantasyTeamId,
    IReadOnlyList<HeadToHeadMatch> matches) : IStandingsTieBreakContext
{
    public Guid SeasonId { get; } = seasonId;

    public int? HeadToHeadLeaguePoints(Guid fantasyTeamIdA, Guid fantasyTeamIdB, Guid forFantasyTeamId)
    {
        var relevant = matches.Where(m =>
            (m.HomeFantasyTeamId == fantasyTeamIdA && m.AwayFantasyTeamId == fantasyTeamIdB) ||
            (m.HomeFantasyTeamId == fantasyTeamIdB && m.AwayFantasyTeamId == fantasyTeamIdA));

        int? total = null;
        foreach (var match in relevant)
        {
            var pointsForRequestedTeam = match.HomeFantasyTeamId == forFantasyTeamId
                ? match.LeaguePointsHome
                : match.LeaguePointsAway;

            if (pointsForRequestedTeam is null)
            {
                continue; // not yet played/scored.
            }

            total = (total ?? 0) + pointsForRequestedTeam.Value;
        }

        return total;
    }

    public SeasonGoalPrediction? SeasonGoalPredictionFor(Guid fantasyTeamId) =>
        predictionsByFantasyTeamId.GetValueOrDefault(fantasyTeamId);
}

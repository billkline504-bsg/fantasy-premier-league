namespace EplFantasy.Reporting;

// Persistence shapes for the Reporting & History context (Architecture v1.15 §5: "read-model
// projections only — no aggregate roots of its own").

/// <summary>
/// BR-313: season-to-date Minutes Played/Games Played/Fantasy Points per Player, backing the
/// Draft Player Pool and Squad View statistics columns. Maps to the <c>player_season_statistics</c>
/// SQL view (physically defined in migrations/V008__scoring.sql, alongside player_performances —
/// see that file's comment for why) — a keyless entity, since it is recomputed, never a directly
/// persisted/updatable table (Architecture §6.6).
/// </summary>
public class PlayerSeasonStatistics
{
    public string EplSeasonIdentifier { get; set; } = null!;
    public Guid PlayerId { get; set; }
    public long MinutesPlayed { get; set; }
    public long GamesPlayed { get; set; }
    public long FantasyPoints { get; set; }
    public Guid AsOfGameweekId { get; set; }
}

using EplFantasy.Leagues;
using Xunit;

namespace EplFantasy.UnitTests.Leagues;

public class SeasonTests
{
    [Fact]
    public void Create_establishes_the_correct_initial_state()
    {
        var seasonId = Guid.NewGuid();
        var leagueId = Guid.NewGuid();
        var startDate = new DateOnly(2026, 8, 15);

        var season = Season.Create(seasonId, leagueId, "2026/27", startDate);

        Assert.Equal(seasonId, season.SeasonId);
        Assert.Equal(leagueId, season.LeagueId);
        Assert.Equal("2026/27", season.EplSeasonIdentifier);
        Assert.Equal(SeasonStatus.Setup, season.Status);
        Assert.Equal(startDate, season.StartDate);
        Assert.Null(season.EndDate);
    }
}

public class SeasonConfigurationTests
{
    [Fact]
    public void CopyFrom_snapshots_every_field_from_the_Leagues_current_configuration()
    {
        var leagueId = Guid.NewGuid();
        var seasonId = Guid.NewGuid();
        var source = LeagueConfiguration.CreateDefault(leagueId, DateTimeOffset.UtcNow);
        source.InitialSquadSize = 30; // a League that has overridden its default before this Season existed
        source.TieBreakRulesetVersion = "v2";
        source.ReplacementSelectionCap = 3;

        var seasonConfiguration = SeasonConfiguration.CopyFrom(seasonId, source);

        Assert.Equal(seasonId, seasonConfiguration.SeasonId);
        Assert.Equal(30, seasonConfiguration.InitialSquadSize);
        Assert.Equal(source.WeeklyRosterSize, seasonConfiguration.WeeklyRosterSize);
        Assert.Equal(source.PositionalMinimumGk, seasonConfiguration.PositionalMinimumGk);
        Assert.Equal(source.PositionalMinimumDef, seasonConfiguration.PositionalMinimumDef);
        Assert.Equal(source.PositionalMinimumMid, seasonConfiguration.PositionalMinimumMid);
        Assert.Equal(source.PositionalMinimumFwd, seasonConfiguration.PositionalMinimumFwd);
        Assert.Equal(source.DraftTimerSecondsInitial, seasonConfiguration.DraftTimerSecondsInitial);
        Assert.Equal(source.DraftTimerSecondsSecondary, seasonConfiguration.DraftTimerSecondsSecondary);
        Assert.Equal(source.DraftTimerSecondsReplacement, seasonConfiguration.DraftTimerSecondsReplacement);
        Assert.Equal(source.SecondaryDraftSelectionsPerTeam, seasonConfiguration.SecondaryDraftSelectionsPerTeam);
        Assert.Equal(source.SecondaryDraftSchedulingOffsetDays, seasonConfiguration.SecondaryDraftSchedulingOffsetDays);
        Assert.Equal(source.GameweekRosterLockOffsetBeforeKickoffMinutes, seasonConfiguration.GameweekRosterLockOffsetBeforeKickoffMinutes);
        Assert.Equal(source.LeaguePointsWin, seasonConfiguration.LeaguePointsWin);
        Assert.Equal(source.LeaguePointsDraw, seasonConfiguration.LeaguePointsDraw);
        Assert.Equal(source.LeaguePointsLoss, seasonConfiguration.LeaguePointsLoss);
        Assert.Equal(source.InvitationExpirationDays, seasonConfiguration.InvitationExpirationDays);
        Assert.Equal(3, seasonConfiguration.ReplacementSelectionCap);
        Assert.Equal(source.GameweekReminderLeadTimeHours, seasonConfiguration.GameweekReminderLeadTimeHours);
        Assert.Equal("v2", seasonConfiguration.TieBreakRulesetVersion);
        Assert.Empty(seasonConfiguration.LockedFields);
    }

    [Fact]
    public void CopyFrom_takes_a_snapshot_not_a_live_reference()
    {
        var source = LeagueConfiguration.CreateDefault(Guid.NewGuid(), DateTimeOffset.UtcNow);
        var seasonConfiguration = SeasonConfiguration.CopyFrom(Guid.NewGuid(), source);

        // BR-293/BR-296: a later change to the League's own defaults must never retroactively
        // alter an already-created Season's copy.
        source.InitialSquadSize = 99;

        Assert.Equal(25, seasonConfiguration.InitialSquadSize);
    }
}

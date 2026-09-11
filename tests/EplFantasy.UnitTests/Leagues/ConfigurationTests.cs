using EplFantasy.Leagues;
using Xunit;

namespace EplFantasy.UnitTests.Leagues;

public class LeagueConfigurationUpdateTests
{
    private static ConfigurationValues ChangedValues(int initialSquadSize = 30) => new(
        InitialSquadSize: initialSquadSize,
        WeeklyRosterSize: 16,
        PositionalMinimumGk: 2,
        PositionalMinimumDef: 4,
        PositionalMinimumMid: 3,
        PositionalMinimumFwd: 2,
        DraftTimerSecondsInitial: 400,
        DraftTimerSecondsSecondary: 400,
        DraftTimerSecondsReplacement: 400,
        SecondaryDraftSelectionsPerTeam: 6,
        SecondaryDraftSchedulingOffsetDays: 2,
        GameweekRosterLockOffsetBeforeKickoffMinutes: 90,
        LeaguePointsWin: 4,
        LeaguePointsDraw: 2,
        LeaguePointsLoss: 1,
        InvitationExpirationDays: 10,
        ReplacementSelectionCap: 5,
        GameweekReminderLeadTimeHours: 48,
        TieBreakRulesetVersion: "v2");

    [Fact]
    public void Update_is_unconditional_no_lock_concept_applies_to_League_level_defaults()
    {
        var configuration = LeagueConfiguration.CreateDefault(Guid.NewGuid(), DateTimeOffset.UtcNow);
        var membershipId = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

        configuration.Update(ChangedValues(), membershipId, now);

        Assert.Equal(30, configuration.InitialSquadSize);
        Assert.Equal(16, configuration.WeeklyRosterSize);
        Assert.Equal("v2", configuration.TieBreakRulesetVersion);
        Assert.Equal(5, configuration.ReplacementSelectionCap);
        Assert.Equal(now, configuration.UpdatedAt);
        Assert.Equal(membershipId, configuration.UpdatedByMembershipId);
    }

    [Fact]
    public void ToValues_round_trips_through_Update()
    {
        var configuration = LeagueConfiguration.CreateDefault(Guid.NewGuid(), DateTimeOffset.UtcNow);
        var changed = ChangedValues();

        configuration.Update(changed, Guid.NewGuid(), DateTimeOffset.UtcNow);

        Assert.Equal(changed, configuration.ToValues());
    }
}

public class SeasonConfigurationApplyUpdateTests
{
    private static SeasonConfiguration NewSeasonConfiguration() =>
        SeasonConfiguration.CopyFrom(Guid.NewGuid(), LeagueConfiguration.CreateDefault(Guid.NewGuid(), DateTimeOffset.UtcNow));

    private static ConfigurationValues ChangedValues(int initialSquadSize = 30) => new(
        InitialSquadSize: initialSquadSize,
        WeeklyRosterSize: 16,
        PositionalMinimumGk: 2,
        PositionalMinimumDef: 4,
        PositionalMinimumMid: 3,
        PositionalMinimumFwd: 2,
        DraftTimerSecondsInitial: 400,
        DraftTimerSecondsSecondary: 400,
        DraftTimerSecondsReplacement: 400,
        SecondaryDraftSelectionsPerTeam: 6,
        SecondaryDraftSchedulingOffsetDays: 2,
        GameweekRosterLockOffsetBeforeKickoffMinutes: 90,
        LeaguePointsWin: 4,
        LeaguePointsDraw: 2,
        LeaguePointsLoss: 1,
        InvitationExpirationDays: 10,
        ReplacementSelectionCap: 5,
        GameweekReminderLeadTimeHours: 48,
        TieBreakRulesetVersion: "v2");

    [Fact]
    public void ApplyUpdate_applies_every_field_when_nothing_is_locked()
    {
        var configuration = NewSeasonConfiguration();

        configuration.ApplyUpdate(ChangedValues());

        Assert.Equal(30, configuration.InitialSquadSize);
        Assert.Equal(16, configuration.WeeklyRosterSize);
        Assert.Equal("v2", configuration.TieBreakRulesetVersion);
    }

    [Fact]
    public void ApplyUpdate_throws_when_a_changed_field_is_locked_and_leaves_every_field_untouched()
    {
        var configuration = NewSeasonConfiguration();
        configuration.Lock(nameof(SeasonConfiguration.InitialSquadSize));

        var exception = Assert.Throws<SeasonConfigurationFieldsLockedException>(() => configuration.ApplyUpdate(ChangedValues()));

        Assert.Contains(nameof(SeasonConfiguration.InitialSquadSize), exception.FieldNames);
        Assert.Equal("season_configuration_fields_locked", exception.ErrorCode);
        Assert.Equal(409, exception.StatusCode);
        // Nothing applied — not even the unlocked fields the same call also carried.
        Assert.Equal(25, configuration.InitialSquadSize);
        Assert.Equal(15, configuration.WeeklyRosterSize);
    }

    [Fact]
    public void ApplyUpdate_succeeds_when_a_locked_field_is_resubmitted_with_its_current_unchanged_value()
    {
        var configuration = NewSeasonConfiguration();
        configuration.Lock(nameof(SeasonConfiguration.InitialSquadSize));

        // Same InitialSquadSize (25, the default) as already stored — every other field changes.
        var values = ChangedValues(initialSquadSize: 25);
        configuration.ApplyUpdate(values);

        Assert.Equal(25, configuration.InitialSquadSize);
        Assert.Equal(16, configuration.WeeklyRosterSize); // the unlocked fields still applied
    }

    [Fact]
    public void ApplyUpdate_reports_every_locked_field_that_actually_changed_not_just_the_first()
    {
        var configuration = NewSeasonConfiguration();
        configuration.Lock(nameof(SeasonConfiguration.InitialSquadSize));
        configuration.Lock(nameof(SeasonConfiguration.WeeklyRosterSize));

        var exception = Assert.Throws<SeasonConfigurationFieldsLockedException>(() => configuration.ApplyUpdate(ChangedValues()));

        Assert.Contains(nameof(SeasonConfiguration.InitialSquadSize), exception.FieldNames);
        Assert.Contains(nameof(SeasonConfiguration.WeeklyRosterSize), exception.FieldNames);
        Assert.Equal(2, exception.FieldNames.Count);
    }

    [Fact]
    public void Lock_is_idempotent()
    {
        var configuration = NewSeasonConfiguration();

        configuration.Lock(nameof(SeasonConfiguration.InitialSquadSize));
        configuration.Lock(nameof(SeasonConfiguration.InitialSquadSize));

        Assert.Single(configuration.LockedFields);
    }
}

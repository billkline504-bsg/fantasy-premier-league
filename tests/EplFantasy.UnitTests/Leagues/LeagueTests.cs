using EplFantasy.Leagues;
using Xunit;

namespace EplFantasy.UnitTests.Leagues;

public class LeagueTests
{
    [Fact]
    public void Create_establishes_the_correct_initial_state()
    {
        var leagueId = Guid.NewGuid();
        var creatorMembershipId = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

        var league = League.Create(leagueId, creatorMembershipId, "Office League", "Bragging rights only", now);

        Assert.Equal(leagueId, league.LeagueId);
        Assert.Equal("Office League", league.Name);
        Assert.Equal("Bragging rights only", league.Description);
        Assert.Equal(LeagueStatus.Active, league.Status);
        Assert.Equal(creatorMembershipId, league.CreatedByMembershipId);
        Assert.Equal(now, league.CreatedAt);
    }

    [Fact]
    public void UpdateDetails_applies_only_the_fields_provided()
    {
        var league = League.Create(Guid.NewGuid(), Guid.NewGuid(), "Original Name", "Original description", DateTimeOffset.UtcNow);

        league.UpdateDetails(name: "New Name", description: null, status: null);

        Assert.Equal("New Name", league.Name);
        Assert.Equal("Original description", league.Description);
        Assert.Equal(LeagueStatus.Active, league.Status);
    }

    [Fact]
    public void UpdateDetails_can_change_status_independently_of_name_and_description()
    {
        var league = League.Create(Guid.NewGuid(), Guid.NewGuid(), "Original Name", "Original description", DateTimeOffset.UtcNow);

        league.UpdateDetails(name: null, description: null, status: LeagueStatus.Archived);

        Assert.Equal("Original Name", league.Name);
        Assert.Equal("Original description", league.Description);
        Assert.Equal(LeagueStatus.Archived, league.Status);
    }

    [Fact]
    public void UpdateDetails_with_every_field_updates_all_three()
    {
        var league = League.Create(Guid.NewGuid(), Guid.NewGuid(), "Original Name", "Original description", DateTimeOffset.UtcNow);

        league.UpdateDetails("New Name", "New description", LeagueStatus.Archived);

        Assert.Equal("New Name", league.Name);
        Assert.Equal("New description", league.Description);
        Assert.Equal(LeagueStatus.Archived, league.Status);
    }
}

public class LeagueMembershipTests
{
    [Fact]
    public void CreateFoundingAdministrator_establishes_an_active_administrator_membership()
    {
        var leagueMembershipId = Guid.NewGuid();
        var leagueId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

        var membership = LeagueMembership.CreateFoundingAdministrator(leagueMembershipId, leagueId, userId, now);

        Assert.Equal(leagueMembershipId, membership.LeagueMembershipId);
        Assert.Equal(leagueId, membership.LeagueId);
        Assert.Equal(userId, membership.UserId);
        Assert.True(membership.IsAdministrator);
        Assert.Equal(MembershipStatus.Active, membership.Status);
        Assert.Equal(now, membership.JoinedAt);
        Assert.Null(membership.LeftAt);
    }

    [Fact]
    public void Join_establishes_an_active_non_administrator_membership()
    {
        var leagueMembershipId = Guid.NewGuid();
        var leagueId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

        var membership = LeagueMembership.Join(leagueMembershipId, leagueId, userId, now);

        Assert.Equal(leagueMembershipId, membership.LeagueMembershipId);
        Assert.Equal(leagueId, membership.LeagueId);
        Assert.Equal(userId, membership.UserId);
        Assert.False(membership.IsAdministrator);
        Assert.Equal(MembershipStatus.Active, membership.Status);
        Assert.Equal(now, membership.JoinedAt);
        Assert.Null(membership.LeftAt);
    }

    [Fact]
    public void Leave_transitions_a_non_administrator_membership_to_Left()
    {
        var now = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
        var membership = LeagueMembership.Join(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), now);

        membership.Leave(now.AddDays(30));

        Assert.Equal(MembershipStatus.Left, membership.Status);
        Assert.Equal(now.AddDays(30), membership.LeftAt);
    }

    [Fact]
    public void Leave_is_idempotent_for_an_already_left_membership()
    {
        var now = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
        var membership = LeagueMembership.Join(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), now);
        membership.Leave(now.AddDays(1));

        membership.Leave(now.AddDays(30));

        // The second call is a no-op — LeftAt is not rewritten to the later timestamp.
        Assert.Equal(now.AddDays(1), membership.LeftAt);
    }

    [Fact]
    public void Leave_throws_for_the_League_Administrators_own_membership()
    {
        var now = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
        var membership = LeagueMembership.CreateFoundingAdministrator(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), now);

        var exception = Assert.Throws<SoleAdministratorCannotLeaveException>(() => membership.Leave(now.AddDays(1)));

        Assert.Equal("sole_administrator_cannot_leave", exception.ErrorCode);
        Assert.Equal(409, exception.StatusCode);
        Assert.Equal(MembershipStatus.Active, membership.Status); // rejected before any state changed
    }

    [Fact]
    public void SetLeagueIcon_sets_the_override_for_an_active_icon()
    {
        var membership = LeagueMembership.Join(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow);
        var iconId = Guid.NewGuid();

        membership.SetLeagueIcon(iconId, profileIconIsActive: true);

        Assert.Equal(iconId, membership.LeagueIconId);
    }

    [Fact]
    public void SetLeagueIcon_throws_for_an_inactive_icon_and_leaves_the_override_unchanged()
    {
        var membership = LeagueMembership.Join(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow);
        var originalIconId = Guid.NewGuid();
        membership.SetLeagueIcon(originalIconId, profileIconIsActive: true);

        var exception = Assert.Throws<LeagueIconNotActiveException>(
            () => membership.SetLeagueIcon(Guid.NewGuid(), profileIconIsActive: false));

        Assert.Equal("profile_icon_not_active", exception.ErrorCode);
        Assert.Equal(400, exception.StatusCode);
        Assert.Equal(originalIconId, membership.LeagueIconId);
    }

    [Fact]
    public void ClearLeagueIcon_removes_the_override()
    {
        var membership = LeagueMembership.Join(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow);
        membership.SetLeagueIcon(Guid.NewGuid(), profileIconIsActive: true);

        membership.ClearLeagueIcon();

        Assert.Null(membership.LeagueIconId);
    }

    [Fact]
    public void ClearLeagueIcon_is_idempotent_when_no_override_was_ever_set()
    {
        var membership = LeagueMembership.Join(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow);

        membership.ClearLeagueIcon();

        Assert.Null(membership.LeagueIconId);
    }
}

public class LeagueConfigurationTests
{
    [Fact]
    public void CreateDefault_matches_BR_291s_stated_defaults()
    {
        var leagueId = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

        var configuration = LeagueConfiguration.CreateDefault(leagueId, now);

        Assert.Equal(leagueId, configuration.LeagueId);
        Assert.Equal(25, configuration.InitialSquadSize);
        Assert.Equal(15, configuration.WeeklyRosterSize);
        Assert.Equal(7, configuration.InvitationExpirationDays);
        Assert.Equal("v1", configuration.TieBreakRulesetVersion);
        Assert.Null(configuration.ReplacementSelectionCap);
        Assert.Equal(now, configuration.UpdatedAt);
        Assert.Null(configuration.UpdatedByMembershipId);
    }
}

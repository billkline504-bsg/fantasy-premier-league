using EplFantasy.Notifications;
using Xunit;

namespace EplFantasy.UnitTests.Notifications;

/// <summary>IT-53 (F-012.1, BR-338): proves the seed set a new LeagueMembership receives — one disabled row per (EventType, Channel) combination, all keyed to that one membership.</summary>
public class NotificationPreferenceSeederTests
{
    [Fact]
    public void SeedFor_creates_one_disabled_row_per_EventType_and_Channel_combination()
    {
        var leagueMembershipId = Guid.NewGuid();

        var seeded = NotificationPreferenceSeeder.SeedFor(leagueMembershipId).ToList();

        var expectedCombinations = Enum.GetValues<NotificationEventType>().Length * Enum.GetValues<NotificationChannel>().Length;
        Assert.Equal(expectedCombinations, seeded.Count);
        Assert.All(seeded, p => Assert.Equal(leagueMembershipId, p.LeagueMembershipId));
        Assert.All(seeded, p => Assert.False(p.Enabled)); // BR-338: disabled by default, never opted-in silently.

        var distinctCombinations = seeded.Select(p => (p.EventType, p.Channel)).Distinct().Count();
        Assert.Equal(expectedCombinations, distinctCombinations); // no duplicates, no gaps.
    }
}

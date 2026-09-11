using EplFantasy.Drafts;
using Xunit;

namespace EplFantasy.UnitTests.Drafts;

/// <summary>
/// IT-48 (F-006.4, BR-287/BR-291): the task breakdown's own required proof (AP-006) — a capped
/// League and an uncapped League behave differently for the exact same already-granted count.
/// </summary>
public class ReplacementOpportunityPolicyTests
{
    [Fact]
    public void CanGrantAnotherOpportunity_is_always_true_when_uncapped()
    {
        // BR-287's own default: null means no fixed per-season cap, regardless of how many have already been granted.
        Assert.True(ReplacementOpportunityPolicy.CanGrantAnotherOpportunity(replacementSelectionCap: null, alreadyGrantedCount: 0));
        Assert.True(ReplacementOpportunityPolicy.CanGrantAnotherOpportunity(replacementSelectionCap: null, alreadyGrantedCount: 1_000));
    }

    [Theory]
    [InlineData(3, 0, true)]
    [InlineData(3, 2, true)]
    [InlineData(3, 3, false)]
    [InlineData(3, 4, false)]
    public void CanGrantAnotherOpportunity_stops_once_a_configured_cap_is_reached(int cap, int alreadyGrantedCount, bool expected)
    {
        Assert.Equal(expected, ReplacementOpportunityPolicy.CanGrantAnotherOpportunity(cap, alreadyGrantedCount));
    }

    [Fact]
    public void CanGrantAnotherOpportunity_a_capped_and_an_uncapped_League_behave_differently_for_the_same_count()
    {
        // AP-006: the exact same alreadyGrantedCount (3) is at the cap for a League configured to
        // exactly 3 (no further grant), yet still fine for an uncapped League.
        const int alreadyGrantedCount = 3;

        Assert.False(ReplacementOpportunityPolicy.CanGrantAnotherOpportunity(replacementSelectionCap: 3, alreadyGrantedCount));
        Assert.True(ReplacementOpportunityPolicy.CanGrantAnotherOpportunity(replacementSelectionCap: null, alreadyGrantedCount));
    }
}

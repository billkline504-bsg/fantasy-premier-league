using EplFantasy.Drafts;
using EplFantasy.Infrastructure;
using Xunit;

namespace EplFantasy.UnitTests.Drafts;

/// <summary>IT-23 (F-005.1, BR-054): proves DraftOrderRandomizer.Shuffle produces a true permutation — same elements, same count, no duplicates or omissions — not merely "looks shuffled."</summary>
public class DraftOrderRandomizerTests
{
    [Fact]
    public void Shuffle_returns_a_true_permutation_of_the_input()
    {
        var fantasyTeamIds = Enumerable.Range(0, 8).Select(_ => Guid.NewGuid()).ToList();

        var shuffled = new DraftOrderRandomizer().Shuffle(fantasyTeamIds);

        Assert.Equal(fantasyTeamIds.Count, shuffled.Count);
        Assert.Equal(fantasyTeamIds.ToHashSet(), shuffled.ToHashSet());
        Assert.Equal(fantasyTeamIds.Distinct().Count(), shuffled.Distinct().Count());
    }

    [Fact]
    public void Shuffle_of_an_empty_list_returns_an_empty_list()
    {
        var shuffled = new DraftOrderRandomizer().Shuffle([]);

        Assert.Empty(shuffled);
    }

    [Fact]
    public void Shuffle_actually_varies_the_order_across_repeated_calls()
    {
        var fantasyTeamIds = Enumerable.Range(0, 10).Select(_ => Guid.NewGuid()).ToList();
        var randomizer = new DraftOrderRandomizer();

        // Not a strict guarantee (a fair shuffle could rarely repeat), but with 10! possible orders
        // the odds of every one of 20 attempts matching the input order are astronomically small —
        // a real regression (e.g. a no-op Shuffle) would fail this every time, not flakily.
        var anyDifferentFromInput = Enumerable.Range(0, 20)
            .Select(_ => randomizer.Shuffle(fantasyTeamIds))
            .Any(shuffled => !shuffled.SequenceEqual(fantasyTeamIds));

        Assert.True(anyDifferentFromInput);
    }
}

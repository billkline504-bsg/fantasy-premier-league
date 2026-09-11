using EplFantasy.Drafts;

namespace EplFantasy.Infrastructure;

/// <summary>A standard Fisher-Yates shuffle (BR-054) — no cryptographic requirement (F-005.1's own remarks), so <see cref="Random.Shared"/> is fine.</summary>
public sealed class DraftOrderRandomizer : IDraftOrderRandomizer
{
    public IReadOnlyList<Guid> Shuffle(IReadOnlyList<Guid> fantasyTeamIds)
    {
        var shuffled = fantasyTeamIds.ToList();

        for (var i = shuffled.Count - 1; i > 0; i--)
        {
            var j = Random.Shared.Next(i + 1);
            (shuffled[i], shuffled[j]) = (shuffled[j], shuffled[i]);
        }

        return shuffled;
    }
}

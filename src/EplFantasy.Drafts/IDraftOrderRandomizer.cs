namespace EplFantasy.Drafts;

/// <summary>
/// IT-23 (F-005.1, BR-054): randomizes the Initial Draft order. Injected — not called directly as
/// `new Random()` inline — purely so a test can substitute a fixed/fake ordering; F-005.1's own
/// remarks explicitly rule out any cryptographic requirement here (a plain shuffle is enough).
/// </summary>
public interface IDraftOrderRandomizer
{
    IReadOnlyList<Guid> Shuffle(IReadOnlyList<Guid> fantasyTeamIds);
}

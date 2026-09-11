namespace EplFantasy.Api.Controllers;

/// <summary>
/// BR-312/BR-316: "any displayed column, toggled ascending/descending by a leading '-'" — the exact
/// same sort contract getSquad (IT-25) and getDraftPlayerPool (IT-28) both expose over their own
/// read-model shape (Architecture §6.3's "one implementation, two read-models," which IT-25's own
/// remarks deferred building until this task existed). Generic over whatever row shape a caller
/// has, via small column-extractor delegates, since SquadPlayerViewDto and DraftPlayerPoolEntryDto
/// are unrelated types with no reason to share a common interface.
/// </summary>
internal static class PlayerPoolSorting
{
    public static List<TDto> Apply<TDto>(
        List<(TDto Dto, string ClubName)> rows,
        string? sort,
        Func<TDto, string> name,
        Func<TDto, string> position,
        Func<TDto, long> minutesPlayed,
        Func<TDto, long> gamesPlayed,
        Func<TDto, long> fantasyPoints)
    {
        if (string.IsNullOrWhiteSpace(sort))
        {
            return rows.Select(r => r.Dto).ToList();
        }

        var descending = sort.StartsWith('-');
        var column = (descending ? sort[1..] : sort).ToLowerInvariant();

        IEnumerable<(TDto Dto, string ClubName)> ordered = column switch
        {
            "name" => descending ? rows.OrderByDescending(r => name(r.Dto)) : rows.OrderBy(r => name(r.Dto)),
            "club" => descending ? rows.OrderByDescending(r => r.ClubName) : rows.OrderBy(r => r.ClubName),
            "position" => descending ? rows.OrderByDescending(r => position(r.Dto)) : rows.OrderBy(r => position(r.Dto)),
            "minutesplayed" => descending ? rows.OrderByDescending(r => minutesPlayed(r.Dto)) : rows.OrderBy(r => minutesPlayed(r.Dto)),
            "gamesplayed" => descending ? rows.OrderByDescending(r => gamesPlayed(r.Dto)) : rows.OrderBy(r => gamesPlayed(r.Dto)),
            "fantasypoints" => descending ? rows.OrderByDescending(r => fantasyPoints(r.Dto)) : rows.OrderBy(r => fantasyPoints(r.Dto)),
            _ => rows,
        };

        return ordered.Select(r => r.Dto).ToList();
    }
}

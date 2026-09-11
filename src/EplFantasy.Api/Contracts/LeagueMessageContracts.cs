using System.ComponentModel.DataAnnotations;
using EplFantasy.Leagues;

namespace EplFantasy.Api.Contracts;

// IT-51 (F-003.6): mirrors createLeagueMessage's exact request body and LeagueMessage's exact
// response schema (see AuthContracts.cs's own header comment on shape/format validation only).

public sealed class CreateLeagueMessageRequest
{
    [Required]
    [StringLength(4000, MinimumLength = 1)]
    public string Body { get; set; } = null!;
}

public sealed class LeagueMessageDto
{
    public required Guid LeagueMessageId { get; init; }
    public required Guid LeagueId { get; init; }
    public required Guid AuthorMembershipId { get; init; }
    public required string Body { get; init; }
    public required DateTimeOffset PublishedAt { get; init; }

    public static LeagueMessageDto From(LeagueMessage message) => new()
    {
        LeagueMessageId = message.LeagueMessageId,
        LeagueId = message.LeagueId,
        AuthorMembershipId = message.AuthorMembershipId,
        Body = message.Body,
        PublishedAt = message.PublishedAt,
    };
}

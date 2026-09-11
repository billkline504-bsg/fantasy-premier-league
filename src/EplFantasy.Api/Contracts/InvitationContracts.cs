using System.ComponentModel.DataAnnotations;

namespace EplFantasy.Api.Contracts;

// IT-04 (F-003.2): shape/format validation only (see AuthContracts.cs's header comment) — mirrors
// the OpenAPI CreateInvitationRequest/Invitation/LeagueMembership schemas exactly.

public sealed class CreateInvitationRequest
{
    [Required]
    public string Destination { get; set; } = null!;

    /// <summary>"Email" or "Sms" — a plain string, not the InvitationChannel enum itself, for the same reason UpdateLeagueRequest.Status is a string (see that type's own remarks); InvitationController parses and validates it.</summary>
    [Required]
    public string Channel { get; set; } = null!;

    public Guid? SeasonId { get; set; }
}

public sealed class InvitationDto
{
    public required Guid InvitationId { get; init; }
    public required Guid LeagueId { get; init; }
    public Guid? SeasonId { get; init; }
    public required string Destination { get; init; }
    public required string Status { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
}

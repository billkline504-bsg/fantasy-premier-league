using System.ComponentModel.DataAnnotations;

namespace EplFantasy.Api.Contracts;

// IT-14 (F-001.3): shape/format validation only (see AuthContracts.cs's header comment) — mirrors
// RegisterRequest.Username's exact attributes (the same OpenAPI Username schema).

public sealed class UpdateUsernameRequest
{
    [Required]
    [StringLength(24, MinimumLength = 3)]
    [RegularExpression("^[A-Za-z0-9_]+$")]
    public string Username { get; set; } = null!;
}

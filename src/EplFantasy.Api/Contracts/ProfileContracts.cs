using System.ComponentModel.DataAnnotations;
using EplFantasy.Identity;

namespace EplFantasy.Api.Contracts;

// IT-09 (F-002.1): shape/format validation only (see AuthContracts.cs's header comment) — mirrors
// the OpenAPI updateDefaultIcon request body and ProfileIcon schema exactly.

public sealed class UpdateDefaultIconRequest
{
    /// <summary>Nullable so [Required] can actually detect a missing value — see CreateSeasonRequest.StartDate's own remarks on why a non-nullable value type can't.</summary>
    [Required]
    public Guid? ProfileIconId { get; set; }
}

public sealed class ProfileIconDto
{
    public required Guid ProfileIconId { get; init; }
    public required string Name { get; init; }
    public required string AssetIdentifier { get; init; }
    public required bool IsActive { get; init; }
    public required int SortOrder { get; init; }

    public static ProfileIconDto From(ProfileIcon icon) => new()
    {
        ProfileIconId = icon.ProfileIconId,
        Name = icon.Name,
        AssetIdentifier = icon.AssetIdentifier,
        IsActive = icon.IsActive,
        SortOrder = icon.SortOrder,
    };
}

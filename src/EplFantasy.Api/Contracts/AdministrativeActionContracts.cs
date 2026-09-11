using System.Text.Json;
using EplFantasy.Administration;

namespace EplFantasy.Api.Contracts;

// IT-52 (F-011.1): mirrors OpenAPI's AdministrativeAction/AdministrativeActionPage schemas exactly.
// BeforeState/AfterState are stored as JSON strings (BeforeStateJson/AfterStateJson) but the
// schema documents them as nested objects, not escaped strings — so this DTO parses them into a
// JsonElement, which System.Text.Json serializes as a genuine nested JSON object.

public sealed class AdministrativeActionDto
{
    public required Guid ActionId { get; init; }
    public required Guid LeagueId { get; init; }
    public Guid? ActingMembershipId { get; init; }
    public required string ActionType { get; init; }
    public required string TargetEntityType { get; init; }
    public required Guid TargetEntityId { get; init; }
    public required JsonElement BeforeState { get; init; }
    public required JsonElement AfterState { get; init; }
    public string? Reason { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }

    public static AdministrativeActionDto From(AdministrativeAction action) => new()
    {
        ActionId = action.ActionId,
        LeagueId = action.LeagueId,
        ActingMembershipId = action.ActingMembershipId,
        ActionType = action.ActionType.ToString(),
        TargetEntityType = action.TargetEntityType,
        TargetEntityId = action.TargetEntityId,
        BeforeState = ParseJson(action.BeforeStateJson),
        AfterState = ParseJson(action.AfterStateJson),
        Reason = action.Reason,
        CreatedAt = action.CreatedAt,
    };

    private static JsonElement ParseJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}

public sealed class AdministrativeActionPageDto
{
    public required IReadOnlyList<AdministrativeActionDto> Items { get; init; }
    public string? NextCursor { get; init; }
}

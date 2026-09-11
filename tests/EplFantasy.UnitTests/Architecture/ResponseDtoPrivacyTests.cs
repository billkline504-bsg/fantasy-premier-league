using EplFantasy.Api.Contracts;
using Xunit;

namespace EplFantasy.UnitTests.Architecture;

/// <summary>
/// IT-16 (F-001.5, BR-015): a single automated check standing in for AC-1/AC-2's "no league-facing
/// endpoint response contains Email/PasswordHash/token" and AC-3's "a shared allow-list/DTO-
/// projection pattern... so a newly added private field cannot leak by default." Every DTO in
/// EplFantasy.Api.Contracts already follows a strict naming convention — a *request* DTO's name
/// always ends in "Request"; everything else is a response DTO the API actually serializes back to
/// a caller — so this scans every response DTO type reflectively rather than maintaining a
/// hand-written per-endpoint allow-list (the two legitimate exceptions, <see cref="UserSelfDto"/>
/// and <see cref="AuthTokenResponse"/>, are the caller's own self-access data, not a league-facing
/// read of someone else's account). A newly added response DTO is covered automatically the moment
/// it's compiled; a newly added Email/PasswordHash/token-shaped property on one fails this test
/// immediately, long before it would reach a real endpoint.
/// </summary>
public class ResponseDtoPrivacyTests
{
    private static readonly HashSet<string> SelfAccessExemptTypeNames = [nameof(UserSelfDto), nameof(AuthTokenResponse)];

    private static readonly string[] ForbiddenPropertyNameSubstrings = ["email", "password", "token"];

    public static IEnumerable<object[]> ResponseDtoTypes() =>
        typeof(UserSelfDto).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, Namespace: "EplFantasy.Api.Contracts" })
            .Where(t => !t.Name.EndsWith("Request", StringComparison.Ordinal))
            .Where(t => !SelfAccessExemptTypeNames.Contains(t.Name))
            .Select(t => new object[] { t });

    [Theory]
    [MemberData(nameof(ResponseDtoTypes))]
    public void Response_DTO_carries_no_private_User_field(Type dtoType)
    {
        var offendingProperties = dtoType.GetProperties()
            .Where(p => ForbiddenPropertyNameSubstrings.Any(forbidden => p.Name.Contains(forbidden, StringComparison.OrdinalIgnoreCase)))
            .Select(p => p.Name)
            .ToArray();

        Assert.True(
            offendingProperties.Length == 0,
            $"{dtoType.Name} exposes {string.Join(", ", offendingProperties)} — BR-015 forbids any " +
            $"league-facing response DTO from carrying Email/PasswordHash/token fields ({nameof(UserSelfDto)}/" +
            $"{nameof(AuthTokenResponse)} are the only exempted self-access DTOs).");
    }
}

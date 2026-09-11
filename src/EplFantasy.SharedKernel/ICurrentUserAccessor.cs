namespace EplFantasy.SharedKernel;

/// <summary>
/// One of the two points where the rest of the system is allowed to depend on "who is logged in"
/// (ADR-006; the other is <c>IAuthenticationService</c>, EplFantasy.Identity). Application services
/// resolve the caller through this abstraction rather than touching <c>HttpContext</c> or a JWT
/// directly — the concrete implementation (reading the <c>sub</c> claim from the current request)
/// lives in EplFantasy.Api, since it is the only layer allowed to know about ASP.NET Core's
/// request pipeline at all.
/// </summary>
public interface ICurrentUserAccessor
{
    /// <summary>The authenticated caller's UserId, or null for an unauthenticated request.</summary>
    Guid? UserId { get; }
}

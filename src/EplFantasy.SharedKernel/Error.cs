namespace EplFantasy.SharedKernel;

/// <summary>
/// A structured, machine-readable failure reason for a <see cref="Result"/> — deliberately shaped
/// to map 1:1 onto OpenAPI Specification v1.0's <c>ProblemDetails.errorCode</c>/<c>detail</c>
/// pair, so an application-service failure can be translated into the API's error contract without
/// an intermediate lookup table.
/// </summary>
/// <param name="Code">A stable, machine-readable identifier (e.g. <c>"invalid_roster_composition"</c>).</param>
/// <param name="Message">A human-readable description suitable for <c>ProblemDetails.detail</c>.</param>
public sealed record Error(string Code, string Message)
{
    /// <summary>The only <see cref="Error"/> value a successful <see cref="Result"/> may carry.</summary>
    public static readonly Error None = new(string.Empty, string.Empty);
}

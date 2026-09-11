namespace EplFantasy.SharedKernel;

/// <summary>
/// Base type for an aggregate rejecting an invalid state transition (Architecture v1.15 §13:
/// "aggregates expose behavior through methods that enforce their own invariants... throws domain
/// exceptions rather than allowing an invalid state to be persisted"). IT-F14's global exception
/// handler maps this exception family to a 400 <c>ProblemDetails</c> response carrying
/// <see cref="ErrorCode"/> as the stable, machine-readable <c>errorCode</c> — concrete subclasses
/// (e.g. a future <c>PlayerAlreadyOwnedException</c> in <c>EplFantasy.Drafts</c>) are defined by the
/// module that owns the invariant, not here, and must supply their own <see cref="ErrorCode"/> the
/// same way <see cref="EplFantasy.SharedKernel.Error"/> pairs a <c>Code</c> with a <c>Message</c> at
/// the application-service boundary.
/// </summary>
public abstract class DomainException : Exception
{
    protected DomainException(string message) : base(message)
    {
    }

    protected DomainException(string message, Exception innerException) : base(message, innerException)
    {
    }

    /// <summary>A stable, machine-readable identifier (e.g. <c>"player_already_owned"</c>) — the exact value the API's ProblemDetails.errorCode carries for this failure.</summary>
    public abstract string ErrorCode { get; }

    /// <summary>
    /// The HTTP status IT-F14's GlobalExceptionHandler reports this failure as. Defaults to 400
    /// (Bad Request) — the common "an invalid state transition was attempted" case — but a rule
    /// whose own contract specifies a different code (e.g. 409 Conflict for a state a well-behaved
    /// caller could legitimately have hit) may override it. A plain <c>int</c>, not
    /// <c>Microsoft.AspNetCore.Http.StatusCodes</c>, since SharedKernel stays framework-agnostic.
    /// </summary>
    public virtual int StatusCode => 400;
}

namespace EplFantasy.SharedKernel;

/// <summary>
/// The outcome of an application-service operation that can fail in an *expected* way (a
/// validation problem, a business rule that legitimately says no) — as opposed to a
/// <see cref="DomainException"/>, which an aggregate throws for a state transition that should
/// never have been attempted by a well-behaved caller. Prefer <see cref="Result"/> at the
/// application-service boundary (where a failure is routine and the API layer needs to report it
/// as a 4xx, not unwind the stack) and reserve exceptions for aggregate invariant violations and
/// truly exceptional conditions.
/// </summary>
public class Result
{
    public bool IsSuccess { get; }

    public bool IsFailure => !IsSuccess;

    public Error Error { get; }

    protected Result(bool isSuccess, Error error)
    {
        if (isSuccess && error != Error.None)
        {
            throw new InvalidOperationException("A successful Result cannot carry an Error.");
        }

        if (!isSuccess && error == Error.None)
        {
            throw new InvalidOperationException("A failed Result must carry a non-None Error.");
        }

        IsSuccess = isSuccess;
        Error = error;
    }

    public static Result Success() => new(true, Error.None);

    public static Result Failure(Error error) => new(false, error);

    public static Result<TValue> Success<TValue>(TValue value) => new(value, true, Error.None);

    public static Result<TValue> Failure<TValue>(Error error) => new(default, false, error);
}

/// <inheritdoc cref="Result"/>
public class Result<TValue> : Result
{
    private readonly TValue? _value;

    protected internal Result(TValue? value, bool isSuccess, Error error) : base(isSuccess, error)
    {
        _value = value;
    }

    /// <exception cref="InvalidOperationException">The result is a failure — check <see cref="Result.IsSuccess"/> first.</exception>
    public TValue Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException("Cannot access Value on a failed Result. Check IsSuccess first.");

    public static implicit operator Result<TValue>(TValue value) => Success(value);

    public static implicit operator Result<TValue>(Error error) => Failure<TValue>(error);
}

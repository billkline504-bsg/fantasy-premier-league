using EplFantasy.SharedKernel;
using Xunit;

namespace EplFantasy.UnitTests.SharedKernel;

public class ResultTests
{
    private static readonly Error SampleError = new("sample_error", "Something went wrong.");

    [Fact]
    public void Success_produces_a_result_with_no_error()
    {
        var result = Result.Success();

        Assert.True(result.IsSuccess);
        Assert.False(result.IsFailure);
        Assert.Equal(Error.None, result.Error);
    }

    [Fact]
    public void Failure_produces_a_result_carrying_the_given_error()
    {
        var result = Result.Failure(SampleError);

        Assert.False(result.IsSuccess);
        Assert.True(result.IsFailure);
        Assert.Equal(SampleError, result.Error);
    }

    // Result's own factory methods (Success/Failure) never call the protected constructor
    // inconsistently, so the guard clauses below are only reachable through a subclass — exactly
    // how a future misbehaving subclass would trip them.
    private sealed class MisusedResult(bool isSuccess, Error error) : Result(isSuccess, error);

    [Fact]
    public void A_successful_result_cannot_be_constructed_with_a_non_None_error()
    {
        Assert.Throws<InvalidOperationException>(() => new MisusedResult(true, SampleError));
    }

    [Fact]
    public void A_failed_result_must_be_constructed_with_a_non_None_error()
    {
        Assert.Throws<InvalidOperationException>(() => new MisusedResult(false, Error.None));
    }

    [Fact]
    public void Success_of_T_carries_the_value_and_is_marked_successful()
    {
        var result = Result.Success(42);

        Assert.True(result.IsSuccess);
        Assert.Equal(42, result.Value);
    }

    [Fact]
    public void Failure_of_T_throws_when_Value_is_accessed()
    {
        var result = Result.Failure<int>(SampleError);

        Assert.True(result.IsFailure);
        Assert.Throws<InvalidOperationException>(() => result.Value);
    }

    [Fact]
    public void A_value_implicitly_converts_to_a_successful_result()
    {
        Result<string> result = "hello";

        Assert.True(result.IsSuccess);
        Assert.Equal("hello", result.Value);
    }

    [Fact]
    public void An_error_implicitly_converts_to_a_failed_result()
    {
        Result<string> result = SampleError;

        Assert.True(result.IsFailure);
        Assert.Equal(SampleError, result.Error);
    }
}

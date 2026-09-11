using EplFantasy.SharedKernel;
using Xunit;

namespace EplFantasy.UnitTests.SharedKernel;

public class DomainExceptionTests
{
    private sealed class TestDomainException(string message) : DomainException(message)
    {
        public override string ErrorCode => "test_domain_failure";
    }

    [Fact]
    public void A_concrete_subclass_exposes_its_own_stable_ErrorCode()
    {
        var exception = new TestDomainException("something an aggregate refused to do");

        Assert.Equal("test_domain_failure", exception.ErrorCode);
        Assert.Equal("something an aggregate refused to do", exception.Message);
    }

    [Fact]
    public void DomainException_is_a_real_Exception_and_carries_an_inner_exception_when_given_one()
    {
        var inner = new InvalidOperationException("root cause");
        var exception = new TestDomainExceptionWithInner("wrapped failure", inner);

        Assert.Same(inner, exception.InnerException);
    }

    private sealed class TestDomainExceptionWithInner(string message, Exception innerException) : DomainException(message, innerException)
    {
        public override string ErrorCode => "test_domain_failure_with_inner";
    }
}

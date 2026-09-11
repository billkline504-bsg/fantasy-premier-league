using EplFantasy.SharedKernel;
using Xunit;

namespace EplFantasy.UnitTests.SharedKernel;

public class ValueObjectTests
{
    private sealed class Money(decimal amount, string currency) : ValueObject
    {
        public decimal Amount { get; } = amount;
        public string Currency { get; } = currency;

        protected override IEnumerable<object?> GetEqualityComponents()
        {
            yield return Amount;
            yield return Currency;
        }
    }

    private sealed class OtherValueObject(decimal amount, string currency) : ValueObject
    {
        // Deliberately identical shape/components to Money, to prove type is part of equality too.
        protected override IEnumerable<object?> GetEqualityComponents()
        {
            yield return amount;
            yield return currency;
        }
    }

    [Fact]
    public void Value_objects_with_the_same_components_are_equal()
    {
        var a = new Money(10.00m, "GBP");
        var b = new Money(10.00m, "GBP");

        Assert.Equal(a, b);
        Assert.True(a == b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Value_objects_with_different_components_are_not_equal()
    {
        var a = new Money(10.00m, "GBP");
        var b = new Money(10.01m, "GBP");
        var c = new Money(10.00m, "USD");

        Assert.NotEqual(a, b);
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void Value_objects_of_different_types_with_identical_components_are_not_equal()
    {
        var a = new Money(10.00m, "GBP");
        var b = new OtherValueObject(10.00m, "GBP");

        Assert.False(a.Equals(b));
    }

    [Fact]
    public void A_value_object_is_never_equal_to_null()
    {
        var a = new Money(10.00m, "GBP");

        Assert.False(a.Equals(null));
        Assert.False(a == null);
        Assert.True(a != null);
    }

    [Fact]
    public void Two_null_value_object_references_are_considered_equal_via_the_operator()
    {
        Money? a = null;
        Money? b = null;

        Assert.True(a == b);
    }
}

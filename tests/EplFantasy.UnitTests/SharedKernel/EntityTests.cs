using EplFantasy.SharedKernel;
using Xunit;

namespace EplFantasy.UnitTests.SharedKernel;

public class EntityTests
{
    private sealed class TestEntity(Guid id) : Entity<Guid>(id)
    {
        public string? Mutable { get; set; }
    }

    private sealed class OtherEntity(Guid id) : Entity<Guid>(id);

    [Fact]
    public void Two_entities_of_the_same_type_and_id_are_equal_regardless_of_other_state()
    {
        var id = Guid.NewGuid();
        var a = new TestEntity(id) { Mutable = "one" };
        var b = new TestEntity(id) { Mutable = "two" };

        Assert.Equal(a, b);
        Assert.True(a == b);
        Assert.False(a != b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Entities_with_different_ids_are_not_equal()
    {
        var a = new TestEntity(Guid.NewGuid());
        var b = new TestEntity(Guid.NewGuid());

        Assert.NotEqual(a, b);
        Assert.False(a == b);
        Assert.True(a != b);
    }

    [Fact]
    public void Entities_of_different_types_sharing_an_id_are_not_equal()
    {
        var id = Guid.NewGuid();
        var a = new TestEntity(id);
        var b = new OtherEntity(id);

        Assert.False(a.Equals(b));
    }

    [Fact]
    public void An_entity_is_never_equal_to_null()
    {
        var a = new TestEntity(Guid.NewGuid());

        Assert.False(a.Equals(null));
        Assert.False(a == null);
        Assert.False(null == a);
    }

    [Fact]
    public void An_entity_is_equal_to_itself_by_reference()
    {
        var a = new TestEntity(Guid.NewGuid());
        var sameReference = a;

        Assert.True(a.Equals(a));
        Assert.True(a == sameReference);
    }
}

using EplFantasy.SharedKernel;
using Xunit;

namespace EplFantasy.UnitTests.SharedKernel;

public class AggregateRootTests
{
    private sealed record SomethingHappened(string Detail) : IDomainEvent;

    private sealed class TestAggregate(Guid id) : AggregateRoot<Guid>(id)
    {
        public void DoSomething(string detail) => Raise(new SomethingHappened(detail));
    }

    [Fact]
    public void A_new_aggregate_has_no_domain_events()
    {
        var aggregate = new TestAggregate(Guid.NewGuid());

        Assert.Empty(aggregate.DomainEvents);
    }

    [Fact]
    public void Raising_an_event_adds_it_to_DomainEvents_in_order()
    {
        var aggregate = new TestAggregate(Guid.NewGuid());

        aggregate.DoSomething("first");
        aggregate.DoSomething("second");

        Assert.Collection(
            aggregate.DomainEvents,
            e => Assert.Equal("first", Assert.IsType<SomethingHappened>(e).Detail),
            e => Assert.Equal("second", Assert.IsType<SomethingHappened>(e).Detail));
    }

    [Fact]
    public void ClearDomainEvents_empties_the_collection()
    {
        var aggregate = new TestAggregate(Guid.NewGuid());
        aggregate.DoSomething("first");

        aggregate.ClearDomainEvents();

        Assert.Empty(aggregate.DomainEvents);
    }

    [Fact]
    public void An_aggregate_root_is_still_an_entity_with_identity_based_equality()
    {
        var id = Guid.NewGuid();
        var a = new TestAggregate(id);
        var b = new TestAggregate(id);

        Assert.Equal(a, b);
    }
}

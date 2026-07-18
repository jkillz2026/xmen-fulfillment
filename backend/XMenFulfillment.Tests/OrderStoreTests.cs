using XMenFulfillment.Api.Models;
using XMenFulfillment.Api.Services;

namespace XMenFulfillment.Tests;

/// <summary>
/// Unit tests for OrderStore capacity and eviction behaviour.
/// </summary>
public class OrderStoreTests
{
    private static FulfillmentContext MakeContext(string orderId) =>
        new()
        {
            Order = new Order(orderId, "C1", "test@test.com",
                [new OrderItem("X-001", "Visor", 1, 9.99m)],
                new ShippingAddress("1 St", "NYC", "NY", "10001", "US"), "pm_ok")
        };

    [Fact]
    public void Save_AndTryGet_RoundTrips()
    {
        var store = new OrderStore();
        var ctx   = MakeContext("ORD-1");

        store.Save("ORD-1", ctx);

        Assert.True(store.TryGet("ORD-1", out var retrieved));
        Assert.Same(ctx, retrieved);
    }

    [Fact]
    public void TryGet_MissingOrder_ReturnsFalse()
    {
        var store = new OrderStore();
        Assert.False(store.TryGet("ORD-GHOST", out _));
    }

    [Fact]
    public void Save_Update_DoesNotDuplicateCount()
    {
        var store = new OrderStore();
        store.Save("ORD-1", MakeContext("ORD-1"));
        store.Save("ORD-1", MakeContext("ORD-1")); // update same key

        Assert.Equal(1, store.Count);
    }

    [Fact]
    public void Save_WhenAtCapacity_EvictsOldestEntry()
    {
        var store = new OrderStore();

        // Fill to capacity
        for (int i = 0; i < OrderStore.MaxCapacity; i++)
            store.Save($"ORD-{i}", MakeContext($"ORD-{i}"));

        Assert.Equal(OrderStore.MaxCapacity, store.Count);

        // Adding one more should evict ORD-0 (the oldest)
        store.Save("ORD-NEW", MakeContext("ORD-NEW"));

        Assert.Equal(OrderStore.MaxCapacity, store.Count); // still capped
        Assert.False(store.TryGet("ORD-0", out _));        // oldest evicted
        Assert.True(store.TryGet("ORD-NEW", out _));       // newest retained
    }

    [Fact]
    public void TryGet_IsCaseInsensitive()
    {
        var store = new OrderStore();
        store.Save("ORD-ABC", MakeContext("ORD-ABC"));

        Assert.True(store.TryGet("ord-abc", out _));
        Assert.True(store.TryGet("ORD-ABC", out _));
    }
}

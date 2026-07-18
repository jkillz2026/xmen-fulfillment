using OpenAI;
using XMenFulfillment.Api.Agents;
using XMenFulfillment.Api.Models;
using XMenFulfillment.Api.Services;

namespace XMenFulfillment.Tests;

/// <summary>
/// Unit tests for WolverineAgent.CompensateAsync — the Saga compensation logic.
///
/// Why this is interesting to test:
///   CompensateAsync is called by Cerebro when Gambit fails AFTER Wolverine already
///   reserved inventory. This test verifies that the release actually happens so stock
///   isn't permanently locked by a failed order.
///
/// How we avoid needing a real OpenAI key:
///   CompensateAsync only touches the InventoryStore — it never calls the chat client.
///   We create an OpenAIClient with a fake key; the constructor succeeds, and since
///   CompensateAsync never makes an HTTP call we never hit the auth error.
///
///   Note: For testing ExecuteAsync (which DOES call OpenAI), you'd want to either:
///   (a) wrap ChatClient behind an IChatClient interface + mock it, or
///   (b) use the OpenAI SDK's built-in testing utilities with recorded responses.
///   Both are valid patterns for production test suites.
/// </summary>
public class WolverineCompensationTests
{
    private static WolverineAgent BuildAgent(InventoryStore inventory)
    {
        // Fake key — safe because CompensateAsync never makes an HTTP call
        var openAiClient = new OpenAIClient("sk-fake-key-for-testing");
        return new WolverineAgent(openAiClient, inventory);
    }

    private static FulfillmentContext BuildContextWithReservations(
        List<(string Sku, int Qty)> reservations)
    {
        var ctx = new FulfillmentContext
        {
            Order = new Order(
                "ORD-COMP-TEST", "CUST-1", "test@test.com",
                reservations.Select(r => new OrderItem(r.Sku, r.Sku, r.Qty, 9.99m)).ToList(),
                new ShippingAddress("1 Main St", "NYC", "NY", "10001", "US"),
                "pm_ok"),
            Status = FulfillmentStatus.Failed
        };
        // Simulate the state left by a successful Wolverine.ExecuteAsync run
        ctx.SharedData["InventoryReserved"] = true;
        ctx.SharedData["ReservedItems"]     = reservations;
        return ctx;
    }

    [Fact]
    public async Task CompensateAsync_ReleasesAllReservedStock()
    {
        var inventory = new InventoryStore();
        var agent     = BuildAgent(inventory);

        // First manually reserve some stock (simulating what ExecuteAsync would have done)
        inventory.Reserve("X-001", 5);
        var availableAfterReserve = inventory.GetAvailable("X-001"); // 100 - 5 = 95

        var ctx = BuildContextWithReservations([("X-001", 5)]);

        await agent.CompensateAsync(ctx);

        // Stock should be restored to what it was before the reservation
        Assert.Equal(availableAfterReserve + 5, inventory.GetAvailable("X-001"));
    }

    [Fact]
    public async Task CompensateAsync_SetsInventoryReservedToFalse()
    {
        var inventory = new InventoryStore();
        inventory.Reserve("X-001", 2);

        var agent = BuildAgent(inventory);
        var ctx   = BuildContextWithReservations([("X-001", 2)]);

        await agent.CompensateAsync(ctx);

        // After compensation, the flag must be cleared so the context reflects reality
        Assert.False((bool)ctx.SharedData["InventoryReserved"]);
    }

    [Fact]
    public async Task CompensateAsync_HandlesMultipleSkus()
    {
        var inventory = new InventoryStore();
        inventory.Reserve("X-001", 3);
        inventory.Reserve("X-002", 2);

        var agent = BuildAgent(inventory);
        var ctx   = BuildContextWithReservations([("X-001", 3), ("X-002", 2)]);

        await agent.CompensateAsync(ctx);

        // Both SKUs should be released
        Assert.Equal(100, inventory.GetAvailable("X-001")); // back to seed value
        Assert.Equal(5,   inventory.GetAvailable("X-002")); // back to seed value
    }

    [Fact]
    public async Task CompensateAsync_WithNoReservations_CompletesWithoutError()
    {
        var inventory = new InventoryStore();
        var agent     = BuildAgent(inventory);

        // Context with no ReservedItems key — simulates a case where Wolverine
        // detected out-of-stock and never wrote to ReservedItems
        var ctx = new FulfillmentContext
        {
            Order = new Order(
                "ORD-EMPTY", "CUST-1", "test@test.com",
                [new OrderItem("X-003", "OOS", 1, 9.99m)],
                new ShippingAddress("1 St", "NYC", "NY", "10001", "US"),
                "pm_ok"),
            Status = FulfillmentStatus.Failed
        };

        var result = await agent.CompensateAsync(ctx);

        Assert.True(result.Success);
    }

    [Fact]
    public async Task CompensateAsync_AddsLogEntryWithOrderId()
    {
        var inventory = new InventoryStore();
        inventory.Reserve("X-001", 1);

        var agent = BuildAgent(inventory);
        var ctx   = BuildContextWithReservations([("X-001", 1)]);

        var result = await agent.CompensateAsync(ctx);

        Assert.Contains("ORD-COMP-TEST", result.Summary);
    }
}

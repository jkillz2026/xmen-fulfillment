using XMenFulfillment.Api.Agents;
using XMenFulfillment.Api.Models;

namespace XMenFulfillment.Tests;

/// <summary>
/// Unit tests for FraudRouter.
///
/// FraudRouter was extracted from Cerebro specifically to make this testing possible.
/// Before the extraction, ApplyFraudRouting was a private method — you'd have to
/// spin up a full Cerebro (requiring an OpenAI client) to test a 10-line if/else.
///
/// Lesson: if a piece of logic is important enough to test, it probably deserves its
/// own class. Testability drives better design.
///
/// These tests cover the three routing bands:
///   score 0–29   → pipeline continues (status unchanged)
///   score 30–69  → AwaitingApproval  (human review)
///   score 70–100 → Failed             (auto-reject)
/// </summary>
public class FraudRouterTests
{
    // Helper: build a minimal FulfillmentContext with a given fraud score already set
    private static FulfillmentContext ContextWithScore(int score)
    {
        var ctx = new FulfillmentContext
        {
            Order = new Order(
                "ORD-TEST", "CUST-1", "test@test.com",
                [new OrderItem("X-001", "Visor", 1, 9.99m)],
                new ShippingAddress("1 Main St", "NYC", "NY", "10001", "US"),
                "pm_ok"),
            Status = FulfillmentStatus.InProgress
        };
        ctx.SharedData["FraudRiskScore"] = score;
        return ctx;
    }

    // ── Low risk (0–29): pipeline must not be interrupted ─────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(29)]
    public void Apply_LowRiskScore_DoesNotChangeStatus(int score)
    {
        var ctx = ContextWithScore(score);
        FraudRouter.Apply(ctx);
        Assert.Equal(FulfillmentStatus.InProgress, ctx.Status);
    }

    [Fact]
    public void Apply_LowRiskScore_AddsNoLogEntry()
    {
        var ctx = ContextWithScore(10);
        FraudRouter.Apply(ctx);
        Assert.Empty(ctx.Log); // Cerebro should stay silent for low-risk orders
    }

    // ── Medium risk (30–69): pause for human review ───────────────────────────

    [Theory]
    [InlineData(30)]
    [InlineData(50)]
    [InlineData(69)]
    public void Apply_MediumRiskScore_SetsAwaitingApproval(int score)
    {
        var ctx = ContextWithScore(score);
        FraudRouter.Apply(ctx);
        Assert.Equal(FulfillmentStatus.AwaitingApproval, ctx.Status);
    }

    [Fact]
    public void Apply_MediumRiskScore_LogsReviewMessage()
    {
        var ctx = ContextWithScore(50);
        FraudRouter.Apply(ctx);

        var entry = Assert.Single(ctx.Log);
        Assert.Equal("Cerebro", entry.AgentName);
        Assert.Equal("FraudReview", entry.Action);
        Assert.True(entry.Success); // paused, but not a failure
        Assert.Contains("50/100", entry.Summary);
    }

    // ── High risk (70–100): auto-reject ───────────────────────────────────────

    [Theory]
    [InlineData(70)]
    [InlineData(85)]
    [InlineData(100)]
    public void Apply_HighRiskScore_SetsFailed(int score)
    {
        var ctx = ContextWithScore(score);
        FraudRouter.Apply(ctx);
        Assert.Equal(FulfillmentStatus.Failed, ctx.Status);
    }

    [Fact]
    public void Apply_HighRiskScore_LogsBlockMessage()
    {
        var ctx = ContextWithScore(75);
        FraudRouter.Apply(ctx);

        var entry = Assert.Single(ctx.Log);
        Assert.Equal("Cerebro", entry.AgentName);
        Assert.Equal("FraudBlock", entry.Action);
        Assert.False(entry.Success);
        Assert.Contains("75/100", entry.Summary);
    }

    // ── Edge: missing score key ───────────────────────────────────────────────

    [Fact]
    public void Apply_NoScoreInSharedData_DefaultsToZeroAndContinues()
    {
        // If Beast somehow didn't write the score, we should fail safe (assume clean)
        var ctx = new FulfillmentContext
        {
            Order = new Order(
                "ORD-TEST", "CUST-1", "test@test.com",
                [new OrderItem("X-001", "Visor", 1, 9.99m)],
                new ShippingAddress("1 Main St", "NYC", "NY", "10001", "US"),
                "pm_ok"),
            Status = FulfillmentStatus.InProgress
        };
        // SharedData["FraudRiskScore"] intentionally not set

        FraudRouter.Apply(ctx);

        Assert.Equal(FulfillmentStatus.InProgress, ctx.Status);
        Assert.Empty(ctx.Log);
    }
}

using XMenFulfillment.Api.Models;

namespace XMenFulfillment.Api.Agents;

/// <summary>
/// FraudRouter — pure static class that maps Beast's risk score to a pipeline decision.
///
/// Extracting this from Cerebro serves two purposes:
///   1. Testability — no need to instantiate Cerebro (which requires OpenAI) to test routing.
///   2. Single Responsibility — Cerebro orchestrates; FraudRouter decides what a score means.
///
/// Risk bands (from the product spec):
///   0–29   → Green  → continue normally
///   30–69  → Yellow → pause for human review (AwaitingApproval)
///   70–100 → Red    → auto-reject (Failed)
/// </summary>
public static class FraudRouter
{
    public static void Apply(FulfillmentContext context)
    {
        // Beast writes "FraudRiskScore" into SharedData as an int (0–100).
        // We default to 0 (safe) if the key is missing for any reason.
        var score = context.SharedData.TryGetValue("FraudRiskScore", out var s) ? (int)s : 0;

        if (score >= 70)
        {
            // Auto-reject — too risky to continue. Cerebro will mark the pipeline Failed.
            context.Status = FulfillmentStatus.Failed;
            context.Log.Add(new AgentMessage(
                "Cerebro", "FraudBlock",
                $"Order REJECTED — fraud risk too high ({score}/100). Pipeline stopped.",
                false, DateTimeOffset.UtcNow));
        }
        else if (score >= 30)
        {
            // Pause for human review. The pipeline stops here until a human calls
            // POST /orders/{id}/approve or /orders/{id}/reject.
            context.Status = FulfillmentStatus.AwaitingApproval;
            context.Log.Add(new AgentMessage(
                "Cerebro", "FraudReview",
                $"Order PAUSED — moderate fraud risk ({score}/100). Awaiting human approval before payment.",
                true, DateTimeOffset.UtcNow));
        }
        // score < 30: no action — pipeline continues normally
    }
}

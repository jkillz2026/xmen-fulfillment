namespace XMenFulfillment.Api.Models;

/// <summary>
/// Typed extension methods for <see cref="FulfillmentContext.SharedData"/>.
///
/// Why these exist:
///   SharedData is a raw Dictionary&lt;string, object&gt; so any agent can store any value
///   without coupling agents to each other (loose coupling). However, raw dictionary access
///   is stringly-typed and easy to get wrong. These extensions provide a typed, discoverable
///   API on top of the dictionary without changing how agents write to it.
///
/// Usage pattern:
///   Writing (in an agent):   context.SharedData["FraudRiskScore"] = 45;
///   Reading (anywhere):      int score = context.GetFraudRiskScore();   ← no magic strings
/// </summary>
public static class FulfillmentContextExtensions
{
    // ── Fraud / Beast ──────────────────────────────────────────────────────────

    /// <summary>Returns Beast's fraud risk score (0–100). Defaults to 0 if not yet set.</summary>
    public static int GetFraudRiskScore(this FulfillmentContext ctx) =>
        ctx.SharedData.TryGetValue("FraudRiskScore", out var v) ? (int)v : 0;

    // ── Inventory / Wolverine ─────────────────────────────────────────────────

    /// <summary>True if Wolverine successfully reserved all items.</summary>
    public static bool IsInventoryReserved(this FulfillmentContext ctx) =>
        ctx.SharedData.TryGetValue("InventoryReserved", out var v) && v is true;

    /// <summary>
    /// The list of (SKU, Quantity) pairs that Wolverine reserved.
    /// Returns an empty list if Wolverine hasn't run yet.
    /// </summary>
    public static List<(string Sku, int Qty)> GetReservedItems(this FulfillmentContext ctx) =>
        ctx.SharedData.TryGetValue("ReservedItems", out var v) && v is List<(string, int)> items
            ? items
            : [];

    // ── Payment / Gambit ──────────────────────────────────────────────────────

    /// <summary>True if Gambit successfully authorized and captured payment.</summary>
    public static bool IsPaymentCaptured(this FulfillmentContext ctx) =>
        ctx.SharedData.TryGetValue("PaymentCaptured", out var v) && v is true;

    /// <summary>The total order value charged. Returns 0 if payment hasn't run.</summary>
    public static decimal GetOrderTotal(this FulfillmentContext ctx) =>
        ctx.SharedData.TryGetValue("OrderTotal", out var v) ? Convert.ToDecimal(v) : 0m;

    /// <summary>Gambit's payment authorization ID. Null if not yet authorized.</summary>
    public static string? GetAuthorizationId(this FulfillmentContext ctx) =>
        ctx.SharedData.TryGetValue("AuthorizationId", out var v) ? v as string : null;

    // ── Shipping / Storm ──────────────────────────────────────────────────────

    /// <summary>The shipment tracking number created by Storm. Null if not yet shipped.</summary>
    public static string? GetTrackingNumber(this FulfillmentContext ctx) =>
        ctx.SharedData.TryGetValue("TrackingNumber", out var v) ? v as string : null;

    /// <summary>The carrier Storm selected (e.g. "USPS", "FedEx").</summary>
    public static string? GetCarrier(this FulfillmentContext ctx) =>
        ctx.SharedData.TryGetValue("Carrier", out var v) ? v as string : null;

    // ── Notifications / Jean Grey ─────────────────────────────────────────────

    /// <summary>True if Jean Grey successfully sent the order confirmation email.</summary>
    public static bool IsEmailSent(this FulfillmentContext ctx) =>
        ctx.SharedData.TryGetValue("EmailSent", out var v) && v is true;
}

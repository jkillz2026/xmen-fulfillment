using System.Collections.Concurrent;

namespace XMenFulfillment.Api.Services;

/// <summary>
/// In-memory inventory store. Tracks stock levels and active reservations per SKU.
/// Pre-seeded with some test data. Phase 9 would swap this for a real DB.
/// </summary>
public class InventoryStore
{
    private readonly ConcurrentDictionary<string, int> _stock =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["X-001"] = 100,
            ["X-002"] = 5,
            ["X-003"] = 0,   // always out of stock — useful for testing
        };

    private readonly ConcurrentDictionary<string, int> _reservations =
        new(StringComparer.OrdinalIgnoreCase);

    public int GetAvailable(string sku)
    {
        var stock = _stock.GetValueOrDefault(sku, 10); // unknown SKUs default to 10
        var reserved = _reservations.GetValueOrDefault(sku, 0);
        return Math.Max(0, stock - reserved);
    }

    public bool Reserve(string sku, int quantity)
    {
        if (GetAvailable(sku) < quantity)
            return false;

        _reservations.AddOrUpdate(sku, quantity, (_, existing) => existing + quantity);
        return true;
    }

    public void Release(string sku, int quantity) =>
        _reservations.AddOrUpdate(sku, 0, (_, existing) => Math.Max(0, existing - quantity));
}

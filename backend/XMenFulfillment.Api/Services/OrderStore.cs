using System.Collections.Concurrent;
using XMenFulfillment.Api.Models;

namespace XMenFulfillment.Api.Services;

/// <summary>
/// In-memory order store with a configurable capacity cap.
///
/// This is a learning-phase store — data lives only for the lifetime of the process.
/// A real system would use a database (e.g. EF Core + PostgreSQL) and would need to
/// handle distributed state if running multiple API instances.
///
/// Eviction strategy: when at capacity, the oldest-inserted order is dropped (FIFO).
/// This is intentionally simple — a production LRU cache would track access time instead.
/// </summary>
public class OrderStore
{
    /// <summary>Maximum number of orders held in memory before eviction starts.</summary>
    public const int MaxCapacity = 500;

    private readonly ConcurrentDictionary<string, FulfillmentContext> _store = new(StringComparer.OrdinalIgnoreCase);

    // A concurrent queue tracks insertion order for FIFO eviction.
    // We keep it in sync with _store on every Save.
    private readonly ConcurrentQueue<string> _insertionOrder = new();

    public void Save(string orderId, FulfillmentContext context)
    {
        _store[orderId] = context;

        // Only enqueue if this is a new key (updates don't change insertion order)
        if (!_insertionOrder.Contains(orderId))
        {
            _insertionOrder.Enqueue(orderId);

            // Evict oldest entry when over capacity
            while (_store.Count > MaxCapacity && _insertionOrder.TryDequeue(out var oldest))
                _store.TryRemove(oldest, out _);
        }
    }

    public bool TryGet(string orderId, out FulfillmentContext? context) =>
        _store.TryGetValue(orderId, out context);

    /// <summary>Number of orders currently held in memory.</summary>
    public int Count => _store.Count;
}

using XMenFulfillment.Api.Services;

namespace XMenFulfillment.Tests;

/// <summary>
/// Unit tests for InventoryStore.
///
/// InventoryStore is the cleanest target for unit tests in this system because:
///   - It has zero external dependencies (no OpenAI, no HTTP, no DB)
///   - Its behaviour is deterministic and easily verifiable
///   - It underpins both Wolverine's reservation logic AND compensation
///
/// Key concept being tested here: the in-memory store correctly tracks
/// available stock = seeded stock - active reservations.
/// </summary>
public class InventoryStoreTests
{
    // ── GetAvailable ──────────────────────────────────────────────────────────

    [Fact]
    public void GetAvailable_KnownInStockSku_ReturnsPreseededValue()
    {
        var store = new InventoryStore();
        // X-001 is pre-seeded with 100 units
        Assert.Equal(100, store.GetAvailable("X-001"));
    }

    [Fact]
    public void GetAvailable_KnownOutOfStockSku_ReturnsZero()
    {
        var store = new InventoryStore();
        // X-003 is pre-seeded with 0 (always out of stock — useful for test scenarios)
        Assert.Equal(0, store.GetAvailable("X-003"));
    }

    [Fact]
    public void GetAvailable_UnknownSku_ReturnsDefaultOfTen()
    {
        var store = new InventoryStore();
        // Unknown SKUs default to 10 so test orders with arbitrary SKUs still work
        Assert.Equal(10, store.GetAvailable("SKU-DOES-NOT-EXIST"));
    }

    [Fact]
    public void GetAvailable_IsCaseInsensitive()
    {
        var store = new InventoryStore();
        Assert.Equal(store.GetAvailable("x-001"), store.GetAvailable("X-001"));
    }

    // ── Reserve ───────────────────────────────────────────────────────────────

    [Fact]
    public void Reserve_WhenStockAvailable_ReturnsTrue()
    {
        var store = new InventoryStore();
        Assert.True(store.Reserve("X-001", 5));
    }

    [Fact]
    public void Reserve_WhenOutOfStock_ReturnsFalse()
    {
        var store = new InventoryStore();
        // X-003 has 0 stock — any reservation attempt should fail
        Assert.False(store.Reserve("X-003", 1));
    }

    [Fact]
    public void Reserve_DecreasesAvailableStock()
    {
        var store = new InventoryStore();
        var before = store.GetAvailable("X-001");
        store.Reserve("X-001", 3);
        Assert.Equal(before - 3, store.GetAvailable("X-001"));
    }

    [Fact]
    public void Reserve_ExactlyAvailableStock_Succeeds()
    {
        var store = new InventoryStore();
        // X-002 has exactly 5 units — reserving all 5 should succeed
        Assert.True(store.Reserve("X-002", 5));
        Assert.Equal(0, store.GetAvailable("X-002"));
    }

    [Fact]
    public void Reserve_MoreThanAvailable_ReturnsFalseAndDoesNotChangeStock()
    {
        var store = new InventoryStore();
        var before = store.GetAvailable("X-002"); // 5
        var result = store.Reserve("X-002", 6);   // requesting 6 > 5 available

        Assert.False(result);
        Assert.Equal(before, store.GetAvailable("X-002")); // stock unchanged
    }

    [Fact]
    public void Reserve_MultipleCallsSameSkuReducesStockCumulatively()
    {
        var store = new InventoryStore();
        store.Reserve("X-001", 10);
        store.Reserve("X-001", 20);
        Assert.Equal(70, store.GetAvailable("X-001")); // 100 - 10 - 20 = 70
    }

    // ── Release ───────────────────────────────────────────────────────────────

    [Fact]
    public void Release_AfterReserve_RestoresAvailableStock()
    {
        var store = new InventoryStore();
        store.Reserve("X-001", 10);
        store.Release("X-001", 10);
        Assert.Equal(100, store.GetAvailable("X-001")); // back to original
    }

    [Fact]
    public void Release_PartialRelease_RestoresOnlyReleasedQuantity()
    {
        var store = new InventoryStore();
        store.Reserve("X-001", 10);
        store.Release("X-001", 4);
        Assert.Equal(94, store.GetAvailable("X-001")); // 100 - 10 + 4 = 94
    }

    [Fact]
    public void Release_WithoutPriorReserve_DoesNotGoNegative()
    {
        var store = new InventoryStore();
        store.Release("X-001", 999); // releasing without having reserved
        // Available should not go above the seeded stock (no negative reservations)
        Assert.Equal(100, store.GetAvailable("X-001"));
    }

    // ── OrderStore capacity ───────────────────────────────────────────────────
    // Grouped here for convenience since OrderStore is also a plain in-memory store.
}

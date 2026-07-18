namespace XMenFulfillment.Api.Models;

public record Order(
    string OrderId,
    string CustomerId,
    string CustomerEmail,
    List<OrderItem> Items,
    ShippingAddress ShippingAddress,
    string PaymentMethodId
);

public record OrderItem(string Sku, string Name, int Quantity, decimal UnitPrice);

public record ShippingAddress(string Line1, string City, string State, string Zip, string Country);

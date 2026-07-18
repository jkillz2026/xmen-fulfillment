using System.Text.Json;
using OpenAI;
using OpenAI.Chat;
using XMenFulfillment.Api.Models;

namespace XMenFulfillment.Api.Agents;

/// <summary>
/// Storm — Shipping Agent
///
/// Demonstrates parallel agent execution (Phase 6):
///   Storm runs concurrently with Jean Grey via Task.WhenAll in Cerebro.
///   Neither depends on the other's output, so they can safely execute simultaneously.
///
/// Tools:
///   get_shipping_rates  — compares rates from multiple mock carriers
///   create_shipment     — creates a label with the selected carrier
/// </summary>
public class StormAgent : IAgent
{
    private readonly ChatClient _chatClient;

    public string Name => "Storm";

    public StormAgent(OpenAIClient openAiClient)
    {
        _chatClient = openAiClient.GetChatClient("gpt-4o-mini");
    }

    public async Task<AgentResult> ExecuteAsync(FulfillmentContext context, string task)
    {
        var order = context.Order;
        var totalWeight = order.Items.Sum(i => i.Quantity * 0.5); // mock weight: 0.5 lbs per item

        var tools = new List<ChatTool>
        {
            ChatTool.CreateFunctionTool(
                functionName: "get_shipping_rates",
                functionDescription: "Returns shipping rates from multiple carriers for the given weight and destination.",
                functionParameters: BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "weightLbs":      { "type": "number" },
                        "destinationZip": { "type": "string" }
                    },
                    "required": ["weightLbs", "destinationZip"]
                }
                """)
            ),
            ChatTool.CreateFunctionTool(
                functionName: "create_shipment",
                functionDescription: "Creates a shipping label with the selected carrier. Returns a tracking number and estimated delivery date.",
                functionParameters: BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "carrier":       { "type": "string", "description": "Carrier name, e.g. USPS, FedEx, UPS" },
                        "serviceLevel":  { "type": "string", "description": "e.g. Ground, Priority, Express" }
                    },
                    "required": ["carrier", "serviceLevel"]
                }
                """)
            )
        };

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage("""
                You are Storm, the shipping agent. Get rates, pick the cheapest option, then create the shipment.
                After completing, respond with a concise one-sentence summary including the tracking number.
                """),
            new UserChatMessage(
                $"Create a shipment for order {order.OrderId}. " +
                $"Destination: {order.ShippingAddress.Zip}, {order.ShippingAddress.Country}. " +
                $"Total weight: {totalWeight:F1} lbs.")
        };

        var options = new ChatCompletionOptions();
        foreach (var tool in tools) options.Tools.Add(tool);

        string trackingNumber = $"TRK-{order.OrderId}-PENDING";
        string carrier = "Unknown";
        string estimatedDelivery = "3-5 business days";

        ChatCompletion completion;
        while (true)
        {
            var result = await _chatClient.CompleteChatAsync(messages, options);
            completion = result.Value;

            if (completion.FinishReason != ChatFinishReason.ToolCalls) break;

            messages.Add(new AssistantChatMessage(completion));

            foreach (var toolCall in completion.ToolCalls)
            {
                string toolResult;
                if (toolCall.FunctionName == "get_shipping_rates")
                {
                    toolResult = RunGetRates(toolCall.FunctionArguments);
                }
                else if (toolCall.FunctionName == "create_shipment")
                {
                    (toolResult, trackingNumber, carrier, estimatedDelivery) =
                        RunCreateShipment(toolCall.FunctionArguments, order.OrderId);
                }
                else
                {
                    toolResult = $"{{\"error\": \"Unknown tool: {toolCall.FunctionName}\"}}";
                }

                messages.Add(new ToolChatMessage(toolCall.Id,
                    [ChatMessageContentPart.CreateTextPart(toolResult)]));
            }
        }

        context.SharedData["TrackingNumber"]      = trackingNumber;
        context.SharedData["Carrier"]             = carrier;
        context.SharedData["EstimatedDelivery"]   = estimatedDelivery;

        var summary = completion.Content[0].Text;
        return new AgentResult(true, summary, new { TrackingNumber = trackingNumber, Carrier = carrier, EstimatedDelivery = estimatedDelivery });
    }

    // ── Tool implementations ──────────────────────────────────────────────────

    private static string RunGetRates(BinaryData args)
    {
        using var doc = JsonDocument.Parse(args);
        var weight = doc.RootElement.TryGetProperty("weightLbs", out var w) ? w.GetDouble() : 1.0;

        // Mock carrier rates — price scales slightly with weight
        var rates = new[]
        {
            new { carrier = "USPS",  serviceLevel = "Ground Advantage", price = Math.Round(6.99 + weight * 0.50, 2), days = 3 },
            new { carrier = "FedEx", serviceLevel = "Ground",           price = Math.Round(8.99 + weight * 0.75, 2), days = 5 },
            new { carrier = "UPS",   serviceLevel = "Ground",           price = Math.Round(9.49 + weight * 0.65, 2), days = 5 },
            new { carrier = "USPS",  serviceLevel = "Priority Mail",    price = Math.Round(9.99 + weight * 0.40, 2), days = 2 },
        };

        return JsonSerializer.Serialize(new { rates });
    }

    private static (string json, string tracking, string carrier, string delivery) RunCreateShipment(
        BinaryData args, string orderId)
    {
        using var doc = JsonDocument.Parse(args);
        var carrier      = doc.RootElement.TryGetProperty("carrier",      out var c) ? c.GetString() ?? "USPS"   : "USPS";
        var serviceLevel = doc.RootElement.TryGetProperty("serviceLevel", out var s) ? s.GetString() ?? "Ground" : "Ground";

        var tracking = $"TRK-{orderId}-{Guid.NewGuid().ToString()[..8].ToUpper()}";
        var deliveryDate = DateTime.UtcNow.AddDays(serviceLevel.Contains("Priority") ? 2 : 5)
                                          .ToString("MMM d, yyyy");

        var json = JsonSerializer.Serialize(new
        {
            trackingNumber    = tracking,
            carrier,
            serviceLevel,
            estimatedDelivery = deliveryDate,
            labelCreated      = true
        });

        return (json, tracking, carrier, deliveryDate);
    }
}

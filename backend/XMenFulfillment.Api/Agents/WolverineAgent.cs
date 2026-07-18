using System.Text.Json;
using OpenAI;
using OpenAI.Chat;
using XMenFulfillment.Api.Models;
using XMenFulfillment.Api.Services;

namespace XMenFulfillment.Api.Agents;

/// <summary>
/// Wolverine — Inventory Agent
///
/// Implements ICompensatable so Cerebro can release reservations
/// if a downstream agent (Gambit) fails — the Saga / compensating transaction pattern.
///
/// Tools:
///   check_stock   — reads available units per SKU
///   reserve_items — reserves units (decrements available stock)
/// </summary>
public class WolverineAgent : IAgent, ICompensatable
{
    private readonly ChatClient _chatClient;
    private readonly InventoryStore _inventory;

    public string Name => "Wolverine";

    public WolverineAgent(OpenAIClient openAiClient, InventoryStore inventory)
    {
        _chatClient = openAiClient.GetChatClient("gpt-4o-mini");
        _inventory  = inventory;
    }

    public async Task<AgentResult> ExecuteAsync(FulfillmentContext context, string task)
    {
        var order = context.Order;

        var tools = new List<ChatTool>
        {
            ChatTool.CreateFunctionTool(
                functionName: "check_stock",
                functionDescription: "Returns the available stock level for a list of SKUs.",
                functionParameters: BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "skus": {
                            "type": "array",
                            "items": { "type": "string" },
                            "description": "List of SKUs to check"
                        }
                    },
                    "required": ["skus"]
                }
                """)
            ),
            ChatTool.CreateFunctionTool(
                functionName: "reserve_items",
                functionDescription: "Reserves the requested quantity of each item. Returns success/failure per SKU.",
                functionParameters: BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "items": {
                            "type": "array",
                            "items": {
                                "type": "object",
                                "properties": {
                                    "sku":      { "type": "string" },
                                    "quantity": { "type": "integer" }
                                },
                                "required": ["sku", "quantity"]
                            }
                        }
                    },
                    "required": ["items"]
                }
                """)
            )
        };

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage("""
                You are Wolverine, the inventory agent. Check stock levels first, then attempt to reserve items.
                If any item is out of stock, do NOT reserve anything and report which items failed.
                After using the tools, respond with a concise one-sentence summary.
                """),
            new UserChatMessage($"Reserve inventory for this order:\n{JsonSerializer.Serialize(order.Items)}")
        };

        var options = new ChatCompletionOptions();
        foreach (var tool in tools) options.Tools.Add(tool);

        var reservedItems = new List<(string Sku, int Qty)>();
        var outOfStock    = new List<string>();
        var allReserved   = true;

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
                if (toolCall.FunctionName == "check_stock")
                {
                    toolResult = RunCheckStock(toolCall.FunctionArguments);
                }
                else if (toolCall.FunctionName == "reserve_items")
                {
                    (toolResult, var reserved, var oos) = RunReserveItems(toolCall.FunctionArguments);
                    reservedItems.AddRange(reserved);
                    outOfStock.AddRange(oos);
                    if (oos.Count > 0) allReserved = false;
                }
                else
                {
                    toolResult = $"{{\"error\": \"Unknown tool: {toolCall.FunctionName}\"}}";
                }

                messages.Add(new ToolChatMessage(toolCall.Id,
                    [ChatMessageContentPart.CreateTextPart(toolResult)]));
            }
        }

        // Post-loop verification: the model may have skipped reserve_items after detecting
        // out-of-stock via check_stock. Catch that by comparing what was reserved vs ordered.
        foreach (var item in order.Items)
        {
            var wasReserved = reservedItems.Any(r => r.Sku.Equals(item.Sku, StringComparison.OrdinalIgnoreCase)
                                                  && r.Qty >= item.Quantity);
            if (!wasReserved && !outOfStock.Contains(item.Sku))
            {
                outOfStock.Add(item.Sku);
                allReserved = false;
            }
        }

        context.SharedData["InventoryReserved"] = allReserved;
        context.SharedData["ReservedItems"]     = reservedItems;

        var summary = completion.Content[0].Text;
        var error   = allReserved ? null : $"Out of stock: {string.Join(", ", outOfStock)}";

        return new AgentResult(allReserved, summary,
            new { Reserved = reservedItems, OutOfStock = outOfStock }, error);
    }

    // ── ICompensatable ────────────────────────────────────────────────────────
    // Called by Cerebro if a downstream agent fails after we already reserved stock.
    public Task<AgentResult> CompensateAsync(FulfillmentContext context)
    {
        if (context.SharedData.TryGetValue("ReservedItems", out var raw) &&
            raw is List<(string Sku, int Qty)> reserved)
        {
            foreach (var (sku, qty) in reserved)
                _inventory.Release(sku, qty);
        }

        var summary = $"Wolverine released inventory reservation for order {context.Order.OrderId}.";
        context.SharedData["InventoryReserved"] = false;
        return Task.FromResult(new AgentResult(true, summary));
    }

    // ── Tool implementations ──────────────────────────────────────────────────

    private string RunCheckStock(BinaryData args)
    {
        using var doc = JsonDocument.Parse(args);
        var skus = doc.RootElement.GetProperty("skus")
                      .EnumerateArray()
                      .Select(s => s.GetString() ?? "")
                      .ToList();

        var levels = skus.ToDictionary(sku => sku, sku => _inventory.GetAvailable(sku));
        return JsonSerializer.Serialize(new { stockLevels = levels });
    }

    private (string json, List<(string, int)> reserved, List<string> oos) RunReserveItems(BinaryData args)
    {
        using var doc = JsonDocument.Parse(args);
        var items = doc.RootElement.GetProperty("items").EnumerateArray().ToList();

        var results  = new Dictionary<string, object>();
        var reserved = new List<(string, int)>();
        var oos      = new List<string>();

        foreach (var item in items)
        {
            var sku = item.GetProperty("sku").GetString() ?? "";
            var qty = item.GetProperty("quantity").GetInt32();

            if (_inventory.Reserve(sku, qty))
            {
                results[sku] = new { reserved = true, quantity = qty };
                reserved.Add((sku, qty));
            }
            else
            {
                results[sku] = new { reserved = false, available = _inventory.GetAvailable(sku) };
                oos.Add(sku);
            }
        }

        return (JsonSerializer.Serialize(new { results }), reserved, oos);
    }
}

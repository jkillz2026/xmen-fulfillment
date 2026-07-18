using System.Text.Json;
using OpenAI;
using OpenAI.Chat;
using XMenFulfillment.Api.Models;

namespace XMenFulfillment.Api.Agents;

/// <summary>
/// Cyclops — Order Validation Agent
///
/// Demonstrates the OpenAI tool-calling (function-calling) pattern:
///
///   1. We define tools as JSON schemas — the model reads these and decides when/how to call them.
///   2. We send the order + tools to GPT-4o-mini.
///   3. If the model wants to call a tool, it returns FinishReason.ToolCalls instead of a text reply.
///   4. We execute the requested tool locally (plain C# functions — no network calls yet).
///   5. We add the tool result back to the message history and call the model again.
///   6. We repeat until FinishReason.Stop — the model's final text is its validation summary.
///
/// Key insight: the model decides WHAT to validate and in what order.
/// We just provide the tools; the intelligence about how to use them lives in GPT.
/// </summary>
public class CyclopsAgent : IAgent
{
    private readonly ChatClient _chatClient;

    private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

    public string Name => "Cyclops";

    public CyclopsAgent(OpenAIClient openAiClient)
    {
        _chatClient = openAiClient.GetChatClient("gpt-4o-mini");
    }

    public async Task<AgentResult> ExecuteAsync(FulfillmentContext context, string task)
    {
        var order = context.Order;

        // ── Build the tool definitions ────────────────────────────────────────
        // These JSON schemas tell the model what arguments each tool accepts.
        var tools = new List<ChatTool>
        {
            ChatTool.CreateFunctionTool(
                functionName: "validate_customer",
                functionDescription: "Validates the customer's email format and that the shipping address is complete.",
                functionParameters: BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "email":   { "type": "string", "description": "Customer email address" },
                        "line1":   { "type": "string", "description": "Street address" },
                        "city":    { "type": "string" },
                        "state":   { "type": "string" },
                        "zip":     { "type": "string" },
                        "country": { "type": "string" }
                    },
                    "required": ["email", "line1", "city", "state", "zip", "country"]
                }
                """)
            ),
            ChatTool.CreateFunctionTool(
                functionName: "validate_items",
                functionDescription: "Validates that all order items have non-empty SKUs, positive quantities, and positive unit prices.",
                functionParameters: BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "items": {
                            "type": "array",
                            "items": {
                                "type": "object",
                                "properties": {
                                    "sku":       { "type": "string" },
                                    "name":      { "type": "string" },
                                    "quantity":  { "type": "integer" },
                                    "unitPrice": { "type": "number" }
                                },
                                "required": ["sku", "name", "quantity", "unitPrice"]
                            }
                        }
                    },
                    "required": ["items"]
                }
                """)
            )
        };

        // ── Seed the conversation ─────────────────────────────────────────────
        var messages = new List<ChatMessage>
        {
            new SystemChatMessage("""
                You are Cyclops, a precision validation agent for eCommerce orders.
                Use BOTH validate_customer and validate_items tools to fully validate the order.
                After calling the tools, respond with a concise one-sentence validation summary.
                If any issues were found, list them briefly.
                """),
            new UserChatMessage($"Validate this order:\n{JsonSerializer.Serialize(order)}")
        };

        var options = new ChatCompletionOptions();
        foreach (var tool in tools)
            options.Tools.Add(tool);

        // ── Agentic tool-call loop ────────────────────────────────────────────
        // This loop is the heart of tool calling:
        //   - Model says "call this tool with these args" → we execute it → we feed result back
        //   - We repeat until the model is done (FinishReason.Stop)
        // while(true) + break avoids having to pre-declare the response variable type.
        var issues = new List<string>();
        var allValid = true;
        ChatCompletion completion;
        while (true)
        {
            var result = await _chatClient.CompleteChatAsync(messages, options);
            completion = result.Value;

            if (completion.FinishReason != ChatFinishReason.ToolCalls)
                break;

            // Add the model's tool-call request to history
            messages.Add(new AssistantChatMessage(completion));

            // Execute each requested tool and add results
            foreach (var toolCall in completion.ToolCalls)
            {
                var toolResult = toolCall.FunctionName switch
                {
                    "validate_customer" => RunValidateCustomer(toolCall.FunctionArguments, issues, ref allValid),
                    "validate_items"    => RunValidateItems(toolCall.FunctionArguments, issues, ref allValid),
                    _                   => $"{{\"error\": \"Unknown tool: {toolCall.FunctionName}\"}}"
                };

                // SDK v2.x: ToolChatMessage takes content parts, not a raw string
                messages.Add(new ToolChatMessage(toolCall.Id,
                    [ChatMessageContentPart.CreateTextPart(toolResult)]));
            }
        }

        // ── Final model summary ───────────────────────────────────────────────
        var summary = completion.Content[0].Text;

        context.SharedData["ValidationPassed"] = allValid;
        context.SharedData["ValidationIssues"] = issues;

        return new AgentResult(allValid, summary, new { AllValid = allValid, Issues = issues },
            allValid ? null : $"Validation failed: {string.Join("; ", issues)}");
    }

    // ── Tool implementations ──────────────────────────────────────────────────
    // These are plain C# functions. The model calls them; we execute them locally.

    private static string RunValidateCustomer(BinaryData args, List<string> issues, ref bool allValid)
    {
        using var doc = JsonDocument.Parse(args);
        var root = doc.RootElement;

        var localIssues = new List<string>();

        var email = root.TryGetProperty("email", out var e) ? e.GetString() ?? "" : "";
        if (!email.Contains('@') || !email.Contains('.'))
            localIssues.Add($"Invalid email format: '{email}'");

        foreach (var field in new[] { "line1", "city", "state", "zip", "country" })
        {
            var val = root.TryGetProperty(field, out var f) ? f.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(val))
                localIssues.Add($"Missing address field: {field}");
        }

        if (localIssues.Count > 0)
        {
            allValid = false;
            issues.AddRange(localIssues);
        }

        return JsonSerializer.Serialize(new
        {
            valid  = localIssues.Count == 0,
            issues = localIssues
        });
    }

    private static string RunValidateItems(BinaryData args, List<string> issues, ref bool allValid)
    {
        using var doc = JsonDocument.Parse(args);
        var root = doc.RootElement;

        var localIssues = new List<string>();

        if (!root.TryGetProperty("items", out var itemsEl) || itemsEl.ValueKind != JsonValueKind.Array)
        {
            localIssues.Add("No items array provided.");
            allValid = false;
            issues.AddRange(localIssues);
            return JsonSerializer.Serialize(new { valid = false, issues = localIssues });
        }

        var i = 0;
        foreach (var item in itemsEl.EnumerateArray())
        {
            var sku = item.TryGetProperty("sku", out var s) ? s.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(sku))
                localIssues.Add($"Item[{i}] has an empty SKU.");

            var qty = item.TryGetProperty("quantity", out var q) ? q.GetInt32() : 0;
            if (qty <= 0)
                localIssues.Add($"Item[{i}] ({sku}) has invalid quantity: {qty}.");

            var price = item.TryGetProperty("unitPrice", out var p) ? p.GetDecimal() : 0m;
            if (price <= 0)
                localIssues.Add($"Item[{i}] ({sku}) has invalid unit price: {price}.");

            i++;
        }

        if (localIssues.Count > 0)
        {
            allValid = false;
            issues.AddRange(localIssues);
        }

        return JsonSerializer.Serialize(new
        {
            valid  = localIssues.Count == 0,
            issues = localIssues
        });
    }
}

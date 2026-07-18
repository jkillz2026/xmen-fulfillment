using System.Text.Json;
using OpenAI;
using OpenAI.Chat;
using XMenFulfillment.Api.Models;

namespace XMenFulfillment.Api.Agents;

/// <summary>
/// Gambit — Payment Agent
///
/// Demonstrates a two-step agentic process managed by the model:
///   1. authorize_payment — holds funds (can fail)
///   2. capture_payment   — finalizes the charge (only if authorization succeeded)
///
/// The model decides whether to call capture based on the authorization result.
/// If Gambit fails, Cerebro triggers Wolverine's compensation (releases inventory).
///
/// Simulated failures (for testing compensation):
///   paymentMethodId starting with "pm_fail"         → authorization fails
///   paymentMethodId starting with "pm_capture_fail" → auth succeeds, capture fails
/// </summary>
public class GambitAgent : IAgent
{
    private readonly ChatClient _chatClient;

    public string Name => "Gambit";

    public GambitAgent(OpenAIClient openAiClient)
    {
        _chatClient = openAiClient.GetChatClient("gpt-4o-mini");
    }

    public async Task<AgentResult> ExecuteAsync(FulfillmentContext context, string task)
    {
        var order = context.Order;
        var total = order.Items.Sum(i => i.Quantity * i.UnitPrice);

        var tools = new List<ChatTool>
        {
            ChatTool.CreateFunctionTool(
                functionName: "authorize_payment",
                functionDescription: "Authorizes a payment for the given amount. Returns an authorizationId on success.",
                functionParameters: BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "paymentMethodId": { "type": "string" },
                        "amount":          { "type": "number", "description": "Amount in USD" }
                    },
                    "required": ["paymentMethodId", "amount"]
                }
                """)
            ),
            ChatTool.CreateFunctionTool(
                functionName: "capture_payment",
                functionDescription: "Captures a previously authorized payment. Call only after a successful authorization.",
                functionParameters: BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "authorizationId": { "type": "string" },
                        "amount":          { "type": "number" }
                    },
                    "required": ["authorizationId", "amount"]
                }
                """)
            )
        };

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage("""
                You are Gambit, the payment agent. Authorize the payment first.
                Only call capture_payment if authorization succeeded.
                If authorization fails, stop and report the failure.
                After completing, respond with a concise one-sentence summary.
                """),
            new UserChatMessage(
                $"Process payment of ${total:F2} using method '{order.PaymentMethodId}' for order {order.OrderId}.")
        };

        var options = new ChatCompletionOptions();
        foreach (var tool in tools) options.Tools.Add(tool);

        string? authorizationId = null;
        var paymentSucceeded = true;
        var failureReason = "";

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
                if (toolCall.FunctionName == "authorize_payment")
                {
                    (toolResult, authorizationId, var authOk, var authError) =
                        RunAuthorize(toolCall.FunctionArguments, order.PaymentMethodId);
                    if (!authOk) { paymentSucceeded = false; failureReason = authError; }
                }
                else if (toolCall.FunctionName == "capture_payment")
                {
                    (toolResult, var captureOk, var captureError) =
                        RunCapture(toolCall.FunctionArguments, order.PaymentMethodId);
                    if (!captureOk) { paymentSucceeded = false; failureReason = captureError; }
                }
                else
                {
                    toolResult = $"{{\"error\": \"Unknown tool: {toolCall.FunctionName}\"}}";
                }

                messages.Add(new ToolChatMessage(toolCall.Id,
                    [ChatMessageContentPart.CreateTextPart(toolResult)]));
            }
        }

        context.SharedData["PaymentCaptured"]  = paymentSucceeded;
        context.SharedData["OrderTotal"]        = total;
        if (authorizationId != null)
            context.SharedData["AuthorizationId"] = authorizationId;

        var summary = completion.Content[0].Text;
        var error   = paymentSucceeded ? null : $"Payment failed: {failureReason}";

        return new AgentResult(paymentSucceeded, summary, new { Total = total, AuthorizationId = authorizationId }, error);
    }

    // ── Tool implementations ──────────────────────────────────────────────────

    private static (string json, string? authId, bool ok, string error) RunAuthorize(
        BinaryData args, string paymentMethodId)
    {
        // Simulate failure for test payment methods
        if (paymentMethodId.StartsWith("pm_fail", StringComparison.OrdinalIgnoreCase))
        {
            var failJson = JsonSerializer.Serialize(new
            {
                success = false,
                error   = "Card declined by issuer."
            });
            return (failJson, null, false, "Card declined by issuer.");
        }

        var authId = $"AUTH-{Guid.NewGuid().ToString()[..8].ToUpper()}";
        var json   = JsonSerializer.Serialize(new { success = true, authorizationId = authId });
        return (json, authId, true, "");
    }

    private static (string json, bool ok, string error) RunCapture(
        BinaryData args, string paymentMethodId)
    {
        // Simulate capture failure (auth succeeded but capture fails — triggers compensation)
        if (paymentMethodId.StartsWith("pm_capture_fail", StringComparison.OrdinalIgnoreCase))
        {
            var failJson = JsonSerializer.Serialize(new
            {
                success = false,
                error   = "Capture rejected — insufficient funds after hold."
            });
            return (failJson, false, "Insufficient funds after hold.");
        }

        var captureId = $"CAP-{Guid.NewGuid().ToString()[..8].ToUpper()}";
        var json      = JsonSerializer.Serialize(new { success = true, captureId });
        return (json, true, "");
    }
}

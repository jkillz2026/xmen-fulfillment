using System.Text.Json;
using OpenAI;
using OpenAI.Chat;
using XMenFulfillment.Api.Models;

namespace XMenFulfillment.Api.Agents;

/// <summary>
/// Jean Grey — Notification Agent
///
/// Runs in PARALLEL with Storm (Phase 6 — Task.WhenAll pattern).
/// Because Jean Grey sends an order confirmation (not a shipping confirmation),
/// she doesn't need Storm's tracking number to do her job.
///
/// In a real system, a second "Shipment Dispatched" email would follow once
/// Storm completes — but for this learning phase, one email is sufficient.
///
/// Tools:
///   compose_confirmation — builds the order confirmation email content
///   send_email           — simulates delivery (logs to console; swap for SendGrid later)
/// </summary>
public class JeanGreyAgent : IAgent
{
    private readonly ChatClient _chatClient;

    public string Name => "Jean Grey";

    public JeanGreyAgent(OpenAIClient openAiClient)
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
                functionName: "compose_confirmation",
                functionDescription: "Composes an order confirmation email with order details and estimated delivery.",
                functionParameters: BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "customerEmail": { "type": "string" },
                        "orderId":       { "type": "string" },
                        "itemsSummary":  { "type": "string", "description": "Brief summary of items ordered" },
                        "orderTotal":    { "type": "number" }
                    },
                    "required": ["customerEmail", "orderId", "itemsSummary", "orderTotal"]
                }
                """)
            ),
            ChatTool.CreateFunctionTool(
                functionName: "send_email",
                functionDescription: "Sends the email. Returns a message ID on success.",
                functionParameters: BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "to":      { "type": "string" },
                        "subject": { "type": "string" },
                        "body":    { "type": "string" }
                    },
                    "required": ["to", "subject", "body"]
                }
                """)
            )
        };

        var itemsSummary = string.Join(", ", order.Items.Select(i => $"{i.Quantity}x {i.Name}"));

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage("""
                You are Jean Grey, the customer notification agent.
                Compose and send a warm, professional order confirmation email.
                Use compose_confirmation first, then send_email.
                After sending, respond with a concise one-sentence summary.
                """),
            new UserChatMessage(
                $"Send an order confirmation for order {order.OrderId} to {order.CustomerEmail}. " +
                $"Items: {itemsSummary}. Total: ${total:F2}.")
        };

        var options = new ChatCompletionOptions();
        foreach (var tool in tools) options.Tools.Add(tool);

        string messageId = "";
        bool emailSent = false;

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
                if (toolCall.FunctionName == "compose_confirmation")
                {
                    toolResult = RunComposeConfirmation(toolCall.FunctionArguments);
                }
                else if (toolCall.FunctionName == "send_email")
                {
                    (toolResult, messageId) = RunSendEmail(toolCall.FunctionArguments);
                    emailSent = true;
                }
                else
                {
                    toolResult = $"{{\"error\": \"Unknown tool: {toolCall.FunctionName}\"}}";
                }

                messages.Add(new ToolChatMessage(toolCall.Id,
                    [ChatMessageContentPart.CreateTextPart(toolResult)]));
            }
        }

        context.SharedData["EmailSent"]    = emailSent;
        context.SharedData["EmailMsgId"]   = messageId;

        var summary = completion.Content[0].Text;
        return new AgentResult(true, summary, new { EmailSent = emailSent, MessageId = messageId });
    }

    // ── Tool implementations ──────────────────────────────────────────────────

    private static string RunComposeConfirmation(BinaryData args)
    {
        using var doc   = JsonDocument.Parse(args);
        var orderId     = doc.RootElement.TryGetProperty("orderId",      out var o) ? o.GetString() ?? "" : "";
        var email       = doc.RootElement.TryGetProperty("customerEmail",out var e) ? e.GetString() ?? "" : "";
        var items       = doc.RootElement.TryGetProperty("itemsSummary", out var i) ? i.GetString() ?? "" : "";
        var total       = doc.RootElement.TryGetProperty("orderTotal",   out var t) ? t.GetDecimal()     : 0m;

        var subject = $"Order Confirmed — {orderId}";
        var body    = $"Thank you for your order!\n\nOrder: {orderId}\nItems: {items}\nTotal: ${total:F2}\n\nWe'll notify you once your order ships.";

        return JsonSerializer.Serialize(new { subject, body, to = email });
    }

    private static (string json, string messageId) RunSendEmail(BinaryData args)
    {
        using var doc = JsonDocument.Parse(args);
        var to      = doc.RootElement.TryGetProperty("to",      out var t) ? t.GetString() ?? "" : "";
        var subject = doc.RootElement.TryGetProperty("subject", out var s) ? s.GetString() ?? "" : "";

        var msgId = $"MSG-{Guid.NewGuid().ToString()[..8].ToUpper()}";

        // In Phase 9, swap this Console.WriteLine for a real email provider (SendGrid, SES, etc.)
        Console.WriteLine($"[Jean Grey] EMAIL SENT → To: {to} | Subject: {subject} | ID: {msgId}");

        return (JsonSerializer.Serialize(new { sent = true, messageId = msgId }), msgId);
    }
}

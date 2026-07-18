using System.Text.Json;
using OpenAI;
using OpenAI.Chat;
using XMenFulfillment.Api.Models;

namespace XMenFulfillment.Api.Agents;

/// <summary>
/// Beast — Fraud Detection Agent
///
/// Demonstrates conditional routing in the Orchestrator pattern:
///
///   Beast's job is ONLY to score risk (0–100). It does not make routing decisions.
///   Cerebro reads the score from SharedData and decides what happens next:
///     - Score  0–29 → continue pipeline normally
///     - Score 30–69 → pause for human review (AwaitingApproval)
///     - Score 70–100 → reject order (Failed)
///
///   This separation keeps agents focused on a single concern and lets the
///   orchestrator own all routing logic.
///
/// Tools:
///   score_order_value_risk  — financial risk based on total and item count
///   score_customer_risk     — customer risk based on email domain and country
/// </summary>
public class BeastAgent : IAgent
{
    private readonly ChatClient _chatClient;

    public string Name => "Beast";

    public BeastAgent(OpenAIClient openAiClient)
    {
        _chatClient = openAiClient.GetChatClient("gpt-4o-mini");
    }

    public async Task<AgentResult> ExecuteAsync(FulfillmentContext context, string task)
    {
        var order = context.Order;

        var tools = new List<ChatTool>
        {
            ChatTool.CreateFunctionTool(
                functionName: "score_order_value_risk",
                functionDescription: "Scores financial risk based on the order total and number of items. Returns a risk score 0–50.",
                functionParameters: BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "orderTotal": { "type": "number",  "description": "Total order value in USD" },
                        "itemCount":  { "type": "integer", "description": "Total number of line items" }
                    },
                    "required": ["orderTotal", "itemCount"]
                }
                """)
            ),
            ChatTool.CreateFunctionTool(
                functionName: "score_customer_risk",
                functionDescription: "Scores customer risk based on email domain and shipping country. Returns a risk score 0–50.",
                functionParameters: BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "customerEmail":   { "type": "string", "description": "Customer email address" },
                        "shippingCountry": { "type": "string", "description": "ISO 2-letter country code" }
                    },
                    "required": ["customerEmail", "shippingCountry"]
                }
                """)
            )
        };

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage("""
                You are Beast, a fraud detection specialist. Your only job is to score order risk.
                Call BOTH scoring tools, then reply with a JSON object:
                { "riskScore": <combined 0-100>, "factors": ["..."] }
                riskScore = sum of the two tool scores, capped at 100.
                Return ONLY valid JSON, no markdown, no explanation.
                """),
            new UserChatMessage($"Score the fraud risk for this order:\n{JsonSerializer.Serialize(order)}")
        };

        var options = new ChatCompletionOptions
        {
            ResponseFormat = ChatResponseFormat.CreateJsonObjectFormat()
        };
        foreach (var tool in tools)
            options.Tools.Add(tool);

        int valueScore = 0, customerScore = 0;
        var riskFactors = new List<string>();

        ChatCompletion completion;
        while (true)
        {
            var result = await _chatClient.CompleteChatAsync(messages, options);
            completion = result.Value;

            if (completion.FinishReason != ChatFinishReason.ToolCalls)
                break;

            messages.Add(new AssistantChatMessage(completion));

            foreach (var toolCall in completion.ToolCalls)
            {
                string toolResult;
                if (toolCall.FunctionName == "score_order_value_risk")
                {
                    (valueScore, var factors) = RunScoreOrderValue(toolCall.FunctionArguments);
                    riskFactors.AddRange(factors);
                    toolResult = JsonSerializer.Serialize(new { score = valueScore, factors });
                }
                else if (toolCall.FunctionName == "score_customer_risk")
                {
                    (customerScore, var factors) = RunScoreCustomer(toolCall.FunctionArguments);
                    riskFactors.AddRange(factors);
                    toolResult = JsonSerializer.Serialize(new { score = customerScore, factors });
                }
                else
                {
                    toolResult = $"{{\"error\": \"Unknown tool: {toolCall.FunctionName}\"}}";
                }

                messages.Add(new ToolChatMessage(toolCall.Id,
                    [ChatMessageContentPart.CreateTextPart(toolResult)]));
            }
        }

        // Parse the model's final JSON summary
        var finalRiskScore = Math.Min(valueScore + customerScore, 100);
        try
        {
            using var doc = JsonDocument.Parse(completion.Content[0].Text);
            if (doc.RootElement.TryGetProperty("riskScore", out var rs))
                finalRiskScore = Math.Clamp(rs.GetInt32(), 0, 100);
        }
        catch { /* use calculated score if parsing fails */ }

        context.SharedData["FraudRiskScore"]   = finalRiskScore;
        context.SharedData["FraudRiskFactors"] = riskFactors;

        var summary = $"Fraud risk score: {finalRiskScore}/100. " +
                      (riskFactors.Count > 0 ? $"Factors: {string.Join(", ", riskFactors)}." : "No risk factors detected.");

        return new AgentResult(true, summary, new { RiskScore = finalRiskScore, Factors = riskFactors });
    }

    // ── Tool implementations ──────────────────────────────────────────────────

    private static (int score, List<string> factors) RunScoreOrderValue(BinaryData args)
    {
        using var doc = JsonDocument.Parse(args);
        var root = doc.RootElement;

        var total = root.TryGetProperty("orderTotal", out var t) ? t.GetDecimal() : 0m;
        var count = root.TryGetProperty("itemCount",  out var c) ? c.GetInt32()   : 0;

        var factors = new List<string>();
        var score = 0;

        if (total > 1000m) { score += 30; factors.Add($"High order total (${total:F0})"); }
        else if (total > 500m) { score += 15; factors.Add($"Elevated order total (${total:F0})"); }

        if (count > 20) { score += 20; factors.Add($"Very high item count ({count})"); }
        else if (count > 10) { score += 10; factors.Add($"High item count ({count})"); }

        return (Math.Min(score, 50), factors);
    }

    private static (int score, List<string> factors) RunScoreCustomer(BinaryData args)
    {
        using var doc = JsonDocument.Parse(args);
        var root = doc.RootElement;

        var email   = root.TryGetProperty("customerEmail",   out var e) ? e.GetString() ?? "" : "";
        var country = root.TryGetProperty("shippingCountry", out var c) ? c.GetString() ?? "" : "";

        var factors = new List<string>();
        var score = 0;

        var domain = email.Contains('@') ? email.Split('@')[1].ToLower() : "";
        var freeProviders = new[] { "mailinator.com", "guerrillamail.com", "tempmail.com", "throwam.com" };
        if (freeProviders.Any(p => domain == p))
        {
            score += 30;
            factors.Add($"Disposable email domain ({domain})");
        }

        var highRiskCountries = new[] { "NG", "RU", "VE", "KP" };
        if (highRiskCountries.Contains(country.ToUpper()))
        {
            score += 20;
            factors.Add($"High-risk shipping country ({country})");
        }

        return (Math.Min(score, 50), factors);
    }
}

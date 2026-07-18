using System.Text.Json;
using OpenAI;
using OpenAI.Chat;
using XMenFulfillment.Api.Models;
using XMenFulfillment.Api.Services;

namespace XMenFulfillment.Api.Agents;

/// <summary>
/// Cerebro — the Orchestrator.
///
/// ┌─────────────────────────────────────────────────────────────────────┐
/// │  REQUEST                                                            │
/// │    POST /orders  ──►  OrdersController  ──►  Cerebro.ExecuteAsync  │
/// └─────────────────────────────────────────────────────────────────────┘
///
/// Cerebro's two responsibilities:
///
///   1. PLAN  (PlanAsync)
///      Sends the order to GPT-4o with a system prompt that lists the available
///      agents and their ordering rules. GPT-4o returns a structured JSON plan:
///        { "steps": [ { "agentName": "Cyclops", "task": "...", "runInParallel": false }, ... ] }
///
///   2. EXECUTE  (ExecutePlanAsync → RunStepAsync)
///      Iterates through the plan steps, dispatching each to the corresponding
///      IAgent via ExecuteAsync. Steps marked runInParallel: true in the same
///      consecutive block are run with Task.WhenAll (Storm + Jean Grey).
///
/// Additional behaviours wired into the execution loop:
///
///   • Fraud routing    — after Beast runs, FraudRouter.Apply checks the risk score.
///                        Score 30-69 → AwaitingApproval (pipeline pauses).
///                        Score 70+   → Failed (pipeline stops immediately).
///
///   • Compensation     — if any step fails, CompensateFailedStepsAsync reverses
///                        work done by agents that implement ICompensatable (Saga pattern).
///                        Currently: Wolverine releases reserved inventory.
///
///   • Human-in-loop    — ResumeAsync re-runs only the remaining steps after a human
///                        approves a paused order. RejectAsync terminates it.
///
///   • Real-time events — every start/complete event is broadcast via AgentBroadcaster
///                        (SignalR) so the frontend updates live.
/// </summary>
public class Cerebro
{
    private readonly ChatClient _chatClient;
    private readonly Dictionary<string, IAgent> _agents;
    private readonly AgentBroadcaster _broadcaster;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private const string SystemPrompt = """
        You are Cerebro, an AI orchestrator for eCommerce order fulfillment.

        Available agents and their responsibilities:
        - Cyclops: Validates order data (customer info, items, shipping address)
        - Beast: Fraud detection and risk scoring — MUST run before Gambit
        - Wolverine: Inventory check and reservation
        - Gambit: Payment authorization and capture
        - Storm: Shipping rate selection and label creation
        - Jean Grey: Customer notifications (order confirmation email) — MUST run last

        Rules:
        - Cyclops MUST be the first step
        - Beast MUST run before Gambit
        - Storm and Jean Grey MUST always run in parallel — set runInParallel: true on BOTH
        - Storm and Jean Grey are always the last two steps

        Given the order, return a JSON fulfillment plan in this exact shape:
        {
          "steps": [
            { "agentName": "AgentName", "task": "Specific task description", "runInParallel": false }
          ]
        }

        Return ONLY valid JSON. No markdown, no explanation, no code fences.
        """;

    public Cerebro(OpenAIClient openAiClient, IEnumerable<IAgent> agents, AgentBroadcaster broadcaster)
    {
        _chatClient  = openAiClient.GetChatClient("gpt-4o");
        _agents      = agents.ToDictionary(a => a.Name, StringComparer.OrdinalIgnoreCase);
        _broadcaster = broadcaster;
    }

    public async Task<FulfillmentContext> ExecuteAsync(Order order)
    {
        var context = new FulfillmentContext
        {
            Order = order,
            Status = FulfillmentStatus.InProgress
        };

        FulfillmentPlan plan;
        try
        {
            plan = await PlanAsync(order, context);
        }
        catch (Exception ex)
        {
            context.Status = FulfillmentStatus.Failed;
            context.Log.Add(new AgentMessage("Cerebro", "Plan", $"Planning failed: {ex.Message}", false, DateTimeOffset.UtcNow));
            return context;
        }

        context.Plan = plan; // stored so ResumeAsync can continue from any paused point

        var planSummary = $"Plan created with {plan.Steps.Count} step(s): {string.Join(" → ", plan.Steps.Select(s => s.AgentName))}";
        context.Log.Add(new AgentMessage("Cerebro", "Plan", planSummary, true, DateTimeOffset.UtcNow));
        await _broadcaster.AgentCompletedAsync(order.OrderId, "Cerebro", "Plan", planSummary, true);

        await ExecutePlanAsync(context, plan);

        // If the pipeline failed, run compensation for any agents that already did work
        if (context.Status == FulfillmentStatus.Failed)
            await CompensateFailedStepsAsync(context);

        if (context.Status == FulfillmentStatus.InProgress)
            context.Status = FulfillmentStatus.Completed;

        await _broadcaster.PipelineCompleteAsync(order.OrderId, context.Status.ToString());
        return context;
    }

    // -------------------------------------------------------------------------
    // Human-in-the-loop: Resume a paused pipeline after human approval
    // -------------------------------------------------------------------------
    public async Task ResumeAsync(FulfillmentContext context)
    {
        if (context.Plan is null)
            throw new InvalidOperationException("Cannot resume — no plan stored on context.");

        // Determine which agents already ran successfully
        var done = context.Log
            .Where(m => m.Success)
            .Select(m => m.AgentName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var remaining = new FulfillmentPlan
        {
            Steps = context.Plan.Steps
                .Where(s => !done.Contains(s.AgentName))
                .ToList()
        };

        context.Status = FulfillmentStatus.InProgress;

        var msg = $"Order approved by human reviewer — resuming pipeline with {remaining.Steps.Count} remaining step(s)."; 
        context.Log.Add(new AgentMessage("Cerebro", "Approved", msg, true, DateTimeOffset.UtcNow));
        await _broadcaster.AgentCompletedAsync(context.Order.OrderId, "Cerebro", "Approved", msg, true);

        await ExecutePlanAsync(context, remaining);

        if (context.Status == FulfillmentStatus.Failed)
            await CompensateFailedStepsAsync(context);

        if (context.Status == FulfillmentStatus.InProgress)
            context.Status = FulfillmentStatus.Completed;

        await _broadcaster.PipelineCompleteAsync(context.Order.OrderId, context.Status.ToString());
    }

    // Human-in-the-loop: Reject a paused pipeline
    public async Task RejectAsync(FulfillmentContext context)
    {
        context.Status = FulfillmentStatus.Failed;
        var msg = "Order REJECTED by human reviewer — pipeline terminated.";
        context.Log.Add(new AgentMessage("Cerebro", "Rejected", msg, false, DateTimeOffset.UtcNow));
        await _broadcaster.AgentCompletedAsync(context.Order.OrderId, "Cerebro", "Rejected", msg, false);
        await _broadcaster.PipelineCompleteAsync(context.Order.OrderId, "Failed");
    }

    // -------------------------------------------------------------------------
    // Step 1: Ask GPT-4o to produce a structured fulfillment plan
    // -------------------------------------------------------------------------
    private async Task<FulfillmentPlan> PlanAsync(Order order, FulfillmentContext context)
    {
        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(SystemPrompt),
            new UserChatMessage($"Plan the fulfillment for this order:\n{JsonSerializer.Serialize(order)}")
        };

        var options = new ChatCompletionOptions
        {
            ResponseFormat = ChatResponseFormat.CreateJsonObjectFormat()
        };

        var response = await _chatClient.CompleteChatAsync(messages, options);
        var json = response.Value.Content[0].Text;

        return JsonSerializer.Deserialize<FulfillmentPlan>(json, _jsonOptions)
               ?? throw new InvalidOperationException("Cerebro received an empty plan from GPT.");
    }

    // -------------------------------------------------------------------------
    // Step 2: Execute the plan — sequential by default, parallel when flagged
    // -------------------------------------------------------------------------
    private async Task ExecutePlanAsync(FulfillmentContext context, FulfillmentPlan plan)
    {
        // Group consecutive steps that share runInParallel = true into batches
        var batches = GroupIntoBatches(plan.Steps);

        foreach (var batch in batches)
        {
            // Stop if a prior step failed OR if Beast flagged the order for human review
            if (context.Status is FulfillmentStatus.Failed or FulfillmentStatus.AwaitingApproval)
                break;

            if (batch.Count == 1)
            {
                await RunStepAsync(context, batch[0]);
            }
            else
            {
                // Parallel batch — run all steps concurrently
                await Task.WhenAll(batch.Select(step => RunStepAsync(context, step)));
            }
        }
    }

    private async Task RunStepAsync(FulfillmentContext context, FulfillmentStep step)
    {
        if (!_agents.TryGetValue(step.AgentName, out var agent))
        {
            context.Log.Add(new AgentMessage(
                "Cerebro", "Dispatch",
                $"Unknown agent '{step.AgentName}' — skipping step.",
                false, DateTimeOffset.UtcNow));
            return;
        }

        await _broadcaster.AgentStartedAsync(context.Order.OrderId, agent.Name, step.Task);

        AgentResult result;
        try
        {
            result = await agent.ExecuteAsync(context, step.Task);
        }
        catch (Exception ex)
        {
            result = new AgentResult(false, $"Agent threw an exception: {ex.Message}");
        }

        context.Log.Add(new AgentMessage(
            agent.Name, step.Task, result.Summary, result.Success, DateTimeOffset.UtcNow));
        await _broadcaster.AgentCompletedAsync(
            context.Order.OrderId, agent.Name, step.Task, result.Summary, result.Success);

        if (!result.Success)
        {
            context.Status = FulfillmentStatus.Failed;
            return;
        }

        // ── Conditional routing after Beast ───────────────────────────────────
        // Key concept: Beast scores risk, Cerebro decides what to do with it.
        // The agent and the orchestrator have separate responsibilities.
        // Beast scores fraud risk; FraudRouter decides what the score means for the pipeline.
        // Keeping routing logic in a separate class makes it independently testable.
        if (agent.Name == "Beast")
            FraudRouter.Apply(context);
    }

    // -------------------------------------------------------------------------
    // Step 3: Compensation — undo work done by ICompensatable agents (Saga pattern)
    // -------------------------------------------------------------------------
    private async Task CompensateFailedStepsAsync(FulfillmentContext context)
    {
        // Find agents that successfully ran AND know how to undo their work.
        // Iterate in reverse so we undo in the opposite order of execution.
        var agentsThatRan = context.Log
            .Where(m => m.Success && _agents.TryGetValue(m.AgentName, out _))
            .Select(m => _agents[m.AgentName])
            .OfType<ICompensatable>()
            .Reverse()
            .ToList();

        foreach (var compensatable in agentsThatRan)
        {
            try
            {
                var result = await compensatable.CompensateAsync(context);
                context.Log.Add(new AgentMessage(
                    ((IAgent)compensatable).Name, "Compensate",
                    result.Summary, result.Success, DateTimeOffset.UtcNow));
            }
            catch (Exception ex)
            {
                context.Log.Add(new AgentMessage(
                    ((IAgent)compensatable).Name, "Compensate",
                    $"Compensation failed: {ex.Message}", false, DateTimeOffset.UtcNow));
            }
        }
    }

    // Groups consecutive steps with RunInParallel=true into the same batch
    private static List<List<FulfillmentStep>> GroupIntoBatches(List<FulfillmentStep> steps)
    {
        var batches = new List<List<FulfillmentStep>>();
        var current = new List<FulfillmentStep>();

        foreach (var step in steps)
        {
            if (step.RunInParallel)
            {
                current.Add(step);
            }
            else
            {
                if (current.Count > 0)
                {
                    batches.Add(current);
                    current = new List<FulfillmentStep>();
                }
                batches.Add(new List<FulfillmentStep> { step });
            }
        }

        if (current.Count > 0)
            batches.Add(current);

        return batches;
    }
}

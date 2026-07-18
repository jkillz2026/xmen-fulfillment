using XMenFulfillment.Api.Models;

namespace XMenFulfillment.Api.Agents;

/// <summary>
/// Contract every worker agent must implement.
///
/// Architecture note — Orchestrator pattern:
///   Agents are WORKERS, not decision-makers. Each agent has one job
///   (validate, score, reserve, charge, ship, notify) and reports the outcome
///   back to Cerebro via <see cref="AgentResult"/>. Cerebro reads the result
///   and decides what to do next — route, pause, compensate, or complete.
///
///   This separation means:
///   - Agents are independently testable (no knowledge of the pipeline).
///   - Adding a new agent never requires touching existing agents.
///   - Cerebro's routing logic is centralised and easy to audit.
///
/// <see cref="ICompensatable"/> is an optional second interface agents can implement
/// if they have side effects that must be undone when a downstream step fails (Saga pattern).
/// </summary>

public interface IAgent
{
    string Name { get; }
    Task<AgentResult> ExecuteAsync(FulfillmentContext context, string task);
}

using XMenFulfillment.Api.Models;

namespace XMenFulfillment.Api.Agents;

/// <summary>
/// Marks an agent as capable of undoing its own work (compensating transaction).
///
/// This is the Saga pattern: in a multi-step pipeline, if a later step fails,
/// earlier steps that already succeeded need to reverse their side effects.
///
/// Example: Wolverine reserved inventory → Gambit failed →
///          Cerebro calls Wolverine.CompensateAsync to release the reservation.
/// </summary>
public interface ICompensatable
{
    Task<AgentResult> CompensateAsync(FulfillmentContext context);
}

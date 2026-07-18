using Microsoft.AspNetCore.SignalR;
using XMenFulfillment.Api.Hubs;

namespace XMenFulfillment.Api.Services;

/// <summary>
/// Wraps IHubContext so Cerebro can broadcast agent events without depending
/// directly on SignalR types — keeps the orchestrator testable.
/// </summary>
public class AgentBroadcaster(IHubContext<AgentHub> hub)
{
    public Task AgentStartedAsync(string orderId, string agentName, string task) =>
        hub.Clients.All.SendAsync("AgentStarted", new
        {
            orderId,
            agentName,
            task,
            timestamp = DateTimeOffset.UtcNow
        });

    public Task AgentCompletedAsync(string orderId, string agentName, string action, string summary, bool success) =>
        hub.Clients.All.SendAsync("AgentCompleted", new
        {
            orderId,
            agentName,
            action,
            summary,
            success,
            timestamp = DateTimeOffset.UtcNow
        });

    public Task PipelineCompleteAsync(string orderId, string status) =>
        hub.Clients.All.SendAsync("PipelineComplete", new
        {
            orderId,
            status,
            timestamp = DateTimeOffset.UtcNow
        });
}

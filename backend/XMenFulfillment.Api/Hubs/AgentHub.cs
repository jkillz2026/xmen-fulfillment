using Microsoft.AspNetCore.SignalR;

namespace XMenFulfillment.Api.Hubs;

/// <summary>
/// AgentHub — SignalR hub clients connect to for real-time agent events.
/// The server pushes events; clients only listen (no inbound messages needed yet).
/// Phase 8 will add a client-to-server method for approving flagged orders.
/// </summary>
public class AgentHub : Hub { }

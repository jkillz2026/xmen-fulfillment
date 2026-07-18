namespace XMenFulfillment.Api.Models;

public record AgentMessage(
    string AgentName,
    string Action,
    string Summary,
    bool Success,
    DateTimeOffset Timestamp
);

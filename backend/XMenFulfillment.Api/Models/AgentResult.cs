namespace XMenFulfillment.Api.Models;

public record AgentResult(
    bool Success,
    string Summary,
    object? Data = null,
    string? Error = null
);

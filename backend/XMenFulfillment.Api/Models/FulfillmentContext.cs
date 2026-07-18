namespace XMenFulfillment.Api.Models;

public class FulfillmentContext
{
    public required Order Order { get; init; }
    public List<AgentMessage> Log { get; } = new();
    public Dictionary<string, object> SharedData { get; } = new();
    public FulfillmentStatus Status { get; set; } = FulfillmentStatus.Pending;
    public FulfillmentPlan? Plan { get; set; }
}

public enum FulfillmentStatus
{
    Pending,
    InProgress,
    AwaitingApproval,
    Completed,
    Failed
}

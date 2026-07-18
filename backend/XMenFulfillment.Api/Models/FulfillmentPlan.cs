namespace XMenFulfillment.Api.Models;

public class FulfillmentPlan
{
    public List<FulfillmentStep> Steps { get; set; } = new();
}

public class FulfillmentStep
{
    public string AgentName { get; set; } = "";
    public string Task { get; set; } = "";
    public bool RunInParallel { get; set; } = false;
}

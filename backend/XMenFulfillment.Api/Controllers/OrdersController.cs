using Microsoft.AspNetCore.Mvc;
using XMenFulfillment.Api.Agents;
using XMenFulfillment.Api.Models;
using XMenFulfillment.Api.Services;

namespace XMenFulfillment.Api.Controllers;

[ApiController]
[Route("[controller]")]
public class OrdersController : ControllerBase
{
    private readonly Cerebro _cerebro;
    private readonly OrderStore _store;

    public OrdersController(Cerebro cerebro, OrderStore store)
    {
        _cerebro = cerebro;
        _store = store;
    }

    /// <summary>
    /// Submit an order for fulfillment. Cerebro will plan and execute the pipeline.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> SubmitOrder([FromBody] Order order)
    {
        if (string.IsNullOrWhiteSpace(order.OrderId))
            return BadRequest("OrderId is required.");

        var context = await _cerebro.ExecuteAsync(order);
        _store.Save(order.OrderId, context);

        return Ok(new
        {
            orderId = order.OrderId,
            status = context.Status.ToString(),
            steps = context.Log
        });
    }

    /// <summary>
    /// Get the fulfillment log for a previously submitted order.
    /// </summary>
    [HttpGet("{orderId}/status")]
    public IActionResult GetStatus(string orderId)
    {
        if (!_store.TryGet(orderId, out var context))
            return NotFound($"Order '{orderId}' not found.");

        return Ok(new
        {
            orderId,
            status = context!.Status.ToString(),
            steps = context.Log
        });
    }

    /// <summary>
    /// Human approves a paused (AwaitingApproval) order.
    /// Cerebro resumes the pipeline from the next unexecuted step.
    /// </summary>
    [HttpPost("{orderId}/approve")]
    public async Task<IActionResult> ApproveOrder(string orderId)
    {
        if (!_store.TryGet(orderId, out var context))
            return NotFound($"Order '{orderId}' not found.");

        if (context!.Status != FulfillmentStatus.AwaitingApproval)
            return BadRequest($"Order is '{context.Status}', not AwaitingApproval.");

        await _cerebro.ResumeAsync(context);
        _store.Save(orderId, context);

        return Ok(new { orderId, status = context.Status.ToString(), steps = context.Log });
    }

    /// <summary>
    /// Human rejects a paused (AwaitingApproval) order.
    /// </summary>
    [HttpPost("{orderId}/reject")]
    public async Task<IActionResult> RejectOrder(string orderId)
    {
        if (!_store.TryGet(orderId, out var context))
            return NotFound($"Order '{orderId}' not found.");

        if (context!.Status != FulfillmentStatus.AwaitingApproval)
            return BadRequest($"Order is '{context.Status}', not AwaitingApproval.");

        await _cerebro.RejectAsync(context);
        _store.Save(orderId, context);

        return Ok(new { orderId, status = context.Status.ToString(), steps = context.Log });
    }
}

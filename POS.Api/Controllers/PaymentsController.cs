using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System;
using System.Threading.Tasks;
using POS.Api.Models;
using POS.Shared.Models;

namespace POS.Api.Controllers;

[Route("api/[controller]")]
[ApiController]
public class PaymentsController(PosDbContext context, POS.Shared.Interfaces.ISequenceService sequenceService) : ControllerBase
{
    [HttpPost]
    public async Task<ActionResult<PaymentResponseDto>> ProcessPayment(PaymentProcessDto dto)
    {
        var order = await context.Orders
            .Include(o => o.Payments)
            .FirstOrDefaultAsync(o => o.OrderId == dto.OrderId);

        if (order == null)
            return NotFound("Order not found.");

        if (order.PaymentStatus == "PAID")
            return Conflict("Order is already paid. Duplicate payment prevented.");

        decimal totalAmount = order.TotalAmount;
        decimal tendered = dto.AmountTendered;

        if (tendered < totalAmount)
            return BadRequest($"Amount tendered ({tendered:C}) is less than total order amount ({totalAmount:C}).");

        decimal change = tendered - totalAmount;

        using var transaction = await context.Database.BeginTransactionAsync();
        try
        {
            var payment = new Payment
            {
                OrderId = order.OrderId,
                PaymentMethod = string.IsNullOrWhiteSpace(dto.PaymentMethod) ? "CASH" : dto.PaymentMethod,
                Amount = totalAmount,
                AmountTendered = tendered,
                ChangeAmount = change,
                PaymentStatus = "SUCCESS",
                ReferenceNumber = dto.ReferenceNumber,
                ProcessedBy = dto.ProcessedBy ?? "system",
                PaidAt = DateTime.UtcNow
            };

            context.Payments.Add(payment);

            order.PaymentStatus = "PAID";
            order.OrderStatus = "COMPLETED";

            await context.SaveChangesAsync();

            // Issue BIR invoice number upon successful payment
            await sequenceService.IssueInvoiceNumberAsync(order.OrderId, order.OutletId);

            await transaction.CommitAsync();

            return Ok(new PaymentResponseDto
            {
                PaymentId = payment.PaymentId,
                OrderId = payment.OrderId,
                PaymentMethod = payment.PaymentMethod,
                Amount = payment.Amount,
                AmountTendered = payment.AmountTendered,
                ChangeAmount = payment.ChangeAmount,
                PaymentStatus = payment.PaymentStatus,
                ReferenceNumber = payment.ReferenceNumber,
                ProcessedBy = payment.ProcessedBy,
                PaidAt = payment.PaidAt
            });
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync();
            return StatusCode(500, $"Internal server error while processing payment: {ex.Message}");
        }
    }
}

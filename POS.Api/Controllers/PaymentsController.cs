using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System;
using System.Linq;
using System.Threading.Tasks;
using POS.Api.Models;
using POS.Shared.Models;

namespace POS.Api.Controllers;

[Route("api/[controller]")]
[ApiController]
[Authorize]
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

        if (User.FindFirst("terminal_id")?.Value != order.TerminalId.ToString() && !User.IsInRole("Admin") && !User.IsInRole("Manager")) return Forbid();
        if (order.PaymentStatus == "PAID")
            return Conflict("Order is already paid. Duplicate payment prevented.");

        using var transaction = await context.Database.BeginTransactionAsync();
        try
        {
            Payment lastPayment;

            if (dto.Splits != null && dto.Splits.Count > 0)
            {
                decimal totalSplitAmount = dto.Splits.Sum(s => s.Amount);
                if (Math.Abs(totalSplitAmount - order.TotalAmount) > 0.01m)
                {
                    await transaction.RollbackAsync();
                    return BadRequest($"Sum of split payment amounts ({totalSplitAmount:C}) does not equal total order amount ({order.TotalAmount:C}).");
                }

                foreach (var split in dto.Splits)
                {
                    if (split.Amount <= 0)
                    {
                        await transaction.RollbackAsync();
                        return BadRequest("Split payment amount must be greater than zero.");
                    }
                    decimal tendered = split.AmountTendered > 0 ? split.AmountTendered : split.Amount;
                    if (tendered < split.Amount)
                    {
                        await transaction.RollbackAsync();
                        return BadRequest($"Amount tendered ({tendered:C}) is less than split amount ({split.Amount:C}).");
                    }
                    decimal change = tendered - split.Amount;

                    var payment = new Payment
                    {
                        OrderId = order.OrderId,
                        PaymentMethod = string.IsNullOrWhiteSpace(split.PaymentMethod) ? "CASH" : split.PaymentMethod,
                        Amount = split.Amount,
                        AmountTendered = tendered,
                        ChangeAmount = change,
                        PaymentStatus = "SUCCESS",
                        ReferenceNumber = split.ReferenceNumber,
                        ProcessedBy = dto.ProcessedBy ?? "system",
                        PaidAt = DateTime.UtcNow
                    };

                    context.Payments.Add(payment);
                    order.Payments.Add(payment);
                }

                lastPayment = order.Payments.OrderByDescending(p => p.PaymentId).FirstOrDefault() ?? order.Payments.First();
            }
            else
            {
                decimal totalAmount = order.TotalAmount;
                decimal tendered = dto.AmountTendered > 0 ? dto.AmountTendered : (dto.Amount > 0 ? dto.Amount : totalAmount);

                if (tendered < totalAmount)
                {
                    await transaction.RollbackAsync();
                    return BadRequest($"Amount tendered ({tendered:C}) is less than total order amount ({totalAmount:C}).");
                }

                decimal change = tendered - totalAmount;

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
                lastPayment = payment;
            }

            if (!string.IsNullOrWhiteSpace(dto.CustomerName)) order.CustomerName = dto.CustomerName.Trim();
            if (!string.IsNullOrWhiteSpace(dto.CustomerAddress)) order.CustomerAddress = dto.CustomerAddress.Trim();
            if (!string.IsNullOrWhiteSpace(dto.CustomerTin)) order.CustomerTin = dto.CustomerTin.Trim();
            if (!string.IsNullOrWhiteSpace(dto.CustomerBusinessStyle)) order.CustomerBusinessStyle = dto.CustomerBusinessStyle.Trim();

            // Verify total payments cover or equal order total
            decimal currentTotalPaid = order.Payments.Where(p => p.PaymentStatus == "SUCCESS").Sum(p => p.Amount);
            if (currentTotalPaid >= order.TotalAmount - 0.01m)
            {
                order.PaymentStatus = "PAID";
                order.OrderStatus = "COMPLETED";
            }

            await context.SaveChangesAsync();

            // Issue BIR invoice number upon successful payment if paid
            if (order.PaymentStatus == "PAID")
            {
                await sequenceService.IssueInvoiceNumberAsync(order.OrderId, order.TerminalId);

                // Accumulate online CASH payments into active CashDrawerShift for accurate shift reconciliation
                decimal cashPaid = order.Payments
                    .Where(p => p.PaymentStatus == "SUCCESS" && string.Equals(p.PaymentMethod, "CASH", StringComparison.OrdinalIgnoreCase))
                    .Sum(p => p.Amount);

                if (cashPaid > 0m)
                {
                    var openShift = await context.CashDrawerShifts
                        .Where(s => s.TerminalId == order.TerminalId && s.Status == "OPEN")
                        .OrderByDescending(s => s.OpenedAt)
                        .FirstOrDefaultAsync();

                    if (openShift != null)
                    {
                        openShift.TotalCashSales += cashPaid;
                        CashDrawerController.RecalculateShiftExpectedBalance(openShift);
                        await context.SaveChangesAsync();
                    }
                }
            }

            await transaction.CommitAsync();

            return Ok(new PaymentResponseDto
            {
                PaymentId = lastPayment.PaymentId,
                OrderId = lastPayment.OrderId,
                PaymentMethod = lastPayment.PaymentMethod,
                Amount = lastPayment.Amount,
                AmountTendered = lastPayment.AmountTendered,
                ChangeAmount = lastPayment.ChangeAmount,
                PaymentStatus = lastPayment.PaymentStatus,
                ReferenceNumber = lastPayment.ReferenceNumber,
                ProcessedBy = lastPayment.ProcessedBy,
                PaidAt = lastPayment.PaidAt
            });
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync();
            return StatusCode(500, $"Internal server error while processing payment: {ex.Message}");
        }
    }
}

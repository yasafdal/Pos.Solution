using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using POS.Api.Models;
using POS.Shared.Models;

namespace POS.Api.Controllers;

[Route("api/[controller]")]
[ApiController]
[Authorize]
public class ReturnsController(PosDbContext context) : ControllerBase
{
    [HttpPost("void")]
    public async Task<ActionResult<VoidResponseDto>> VoidTransaction([FromBody] VoidRequestDto dto, CancellationToken cancellationToken = default)
    {
        if (dto == null || dto.OriginalOrderId <= 0 || string.IsNullOrWhiteSpace(dto.VoidReason))
            return BadRequest("Invalid void request details.");

        using var transaction = await context.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken);
        try
        {
            var order = await context.Orders
                .Include(o => o.OrderItems)
                .Include(o => o.Payments)
                .SingleOrDefaultAsync(o => o.OrderId == dto.OriginalOrderId, cancellationToken);

            if (order == null) return NotFound("Original order not found.");
            if (order.OrderStatus == "VOIDED") return BadRequest("Transaction is already voided.");

            // Verify supervisor authorization strictly by PIN code
            User? approver = null;
            if (!string.IsNullOrWhiteSpace(dto.ApprovedByPinOrUsername))
            {
                var cleanPin = dto.ApprovedByPinOrUsername.Trim();
                approver = await context.Users
                    .SingleOrDefaultAsync(u => u.PinCode == cleanPin, cancellationToken);
            }

            if (approver == null)
            {
                return BadRequest("Supervisor PIN not found or invalid.");
            }

            var approverRole = approver.Role?.Trim() ?? "";
            if (!approverRole.Equals("Admin", StringComparison.OrdinalIgnoreCase) &&
                !approverRole.Equals("Manager", StringComparison.OrdinalIgnoreCase) &&
                !approverRole.Equals("Senior Cashier", StringComparison.OrdinalIgnoreCase) &&
                !approverRole.Equals("SeniorCashier", StringComparison.OrdinalIgnoreCase))
            {
                return BadRequest($"User '{approver.Username}' has role '{approver.Role}', which does not have authorization to approve voids/returns.");
            }

            var cashierId = User.Identity?.Name ?? "system";
            var voidRecord = new TransactionVoid
            {
                OriginalOrderId = order.OrderId,
                OriginalInvoiceNumber = order.InvoiceNumber,
                VoidReason = dto.VoidReason.Trim(),
                RequestedBy = cashierId,
                ApprovedBy = approver.Username,
                RequestedAt = DateTime.UtcNow,
                ApprovedAt = DateTime.UtcNow,
                VoidedAt = DateTime.UtcNow,
                Status = "APPROVED",
                ReversalReference = "VOID-" + Guid.NewGuid().ToString().Substring(0, 8).ToUpper()
            };

            context.TransactionVoids.Add(voidRecord);
            await context.SaveChangesAsync(cancellationToken);

            order.OrderStatus = "VOIDED";
            order.PaymentStatus = "VOIDED";

            // If payments were successful, record offsetting refund records
            decimal cashRefundTotal = 0m;
            foreach (var payment in order.Payments.Where(p => p.PaymentStatus == "SUCCESS"))
            {
                payment.PaymentStatus = "REFUNDED";
                if (string.Equals(payment.PaymentMethod, "CASH", StringComparison.OrdinalIgnoreCase))
                {
                    cashRefundTotal += payment.Amount;
                }
                context.Refunds.Add(new Refund
                {
                    VoidId = voidRecord.VoidId,
                    OriginalPaymentId = payment.PaymentId,
                    RefundMethod = payment.PaymentMethod,
                    RefundAmount = payment.Amount,
                    ReferenceNumber = voidRecord.ReversalReference,
                    Status = "COMPLETED",
                    RequestedAt = DateTime.UtcNow,
                    CompletedAt = DateTime.UtcNow
                });
            }

            if (cashRefundTotal > 0m)
            {
                var openShift = await context.CashDrawerShifts
                    .Where(s => s.TerminalId == order.TerminalId && s.Status == "OPEN")
                    .OrderByDescending(s => s.OpenedAt)
                    .FirstOrDefaultAsync(cancellationToken);

                if (openShift != null)
                {
                    openShift.TotalCashRefunds += cashRefundTotal;
                    CashDrawerController.RecalculateShiftExpectedBalance(openShift);
                }
            }

            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return Ok(new VoidResponseDto
            {
                VoidId = voidRecord.VoidId,
                OriginalOrderId = order.OrderId,
                OriginalInvoiceNumber = order.InvoiceNumber,
                VoidReason = voidRecord.VoidReason,
                RequestedBy = voidRecord.RequestedBy,
                ApprovedBy = voidRecord.ApprovedBy,
                VoidedAt = voidRecord.VoidedAt,
                Status = voidRecord.Status,
                ReversalReference = voidRecord.ReversalReference
            });
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync(cancellationToken);
            return StatusCode(500, "Void execution error: " + ex.Message);
        }
    }

    [HttpGet("voids")]
    public async Task<ActionResult<IEnumerable<VoidResponseDto>>> GetVoids(
        [FromQuery] int? branchId,
        [FromQuery] int? terminalId,
        CancellationToken cancellationToken = default,
        [FromQuery] int? outletId = null)
    {
        if (!User.IsInRole("Admin") && int.TryParse(User.FindFirst("branch_id")?.Value, out int claimBranchId))
        {
            if (branchId.HasValue && branchId.Value != claimBranchId)
                return Forbid();
            branchId = claimBranchId;
        }

        var query = context.TransactionVoids
            .AsNoTracking()
            .Include(v => v.OriginalOrder)
            .Include(v => v.Refunds)
            .AsQueryable();

        if (branchId.HasValue)
            query = query.Where(v => v.OriginalOrder != null && v.OriginalOrder.BranchId == branchId.Value);

        if (outletId.HasValue)
        {
            int outId = outletId.Value;
            query = query.Where(v => v.OriginalOrder != null && (v.OriginalOrder.OutletId == outId
                || context.Terminals.Any(t => t.TerminalId == v.OriginalOrder.TerminalId && t.OutletId == outId)));
        }

        if (terminalId.HasValue)
            query = query.Where(v => v.OriginalOrder != null && v.OriginalOrder.TerminalId == terminalId.Value);

        var voids = await query
            .OrderByDescending(v => v.VoidedAt)
            .ToListAsync(cancellationToken);

        var branchesById = await context.Branches.AsNoTracking().ToDictionaryAsync(b => b.BranchId, cancellationToken);
        var outletsById = await context.Outlets.AsNoTracking().ToDictionaryAsync(o => o.OutletId, cancellationToken);
        var terminalsById = await context.Terminals.AsNoTracking().ToDictionaryAsync(t => t.TerminalId, cancellationToken);

        var dtos = voids.Select(v =>
        {
            int brId = v.OriginalOrder?.BranchId ?? 0;
            int termId = v.OriginalOrder?.TerminalId ?? 0;
            terminalsById.TryGetValue(termId, out var term);
            int outId = term != null && term.OutletId > 0 ? term.OutletId : (v.OriginalOrder?.OutletId ?? 0);
            string outName = outletsById.TryGetValue(outId, out var outl)
                ? outl.Name
                : (outId > 0 ? $"Outlet #{outId}" : "Unknown / Legacy Outlet");
            if (brId <= 0 && term != null) brId = term.BranchId;
            string brName = branchesById.TryGetValue(brId, out var br)
                ? br.Name
                : (brId > 0 ? $"Branch #{brId}" : "Unknown Branch");
            string tCode = term?.TerminalCode ?? (termId > 0 ? $"LEGACY-{termId}" : "LEGACY");
            string tName = term?.Name ?? (termId > 0 ? $"Unknown / Legacy Terminal (#{termId})" : "Unknown / Legacy Terminal");
            decimal voidAmt = v.OriginalOrder?.TotalAmount ?? v.Refunds.Sum(rf => rf.RefundAmount);

            return new VoidResponseDto
            {
                VoidId = v.VoidId,
                OriginalOrderId = v.OriginalOrderId,
                OriginalInvoiceNumber = v.OriginalInvoiceNumber ?? v.OriginalOrder?.InvoiceNumber,
                BranchId = brId,
                BranchName = brName,
                OutletId = outId,
                OutletName = outName,
                TerminalId = termId,
                TerminalCode = tCode,
                TerminalName = tName,
                VoidedAmount = voidAmt,
                VoidReason = v.VoidReason,
                RequestedBy = v.RequestedBy,
                ApprovedBy = v.ApprovedBy,
                VoidedAt = v.VoidedAt,
                Status = v.Status,
                ReversalReference = v.ReversalReference
            };
        }).ToList();

        return Ok(dtos);
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<ReturnResponseDto>>> GetReturns(
        [FromQuery] int? branchId,
        [FromQuery] int? terminalId,
        CancellationToken cancellationToken = default,
        [FromQuery] int? outletId = null)
    {
        if (!User.IsInRole("Admin") && int.TryParse(User.FindFirst("branch_id")?.Value, out int claimBranchId))
        {
            if (branchId.HasValue && branchId.Value != claimBranchId)
                return Forbid();
            branchId = claimBranchId;
        }

        var query = context.SalesReturns
            .AsNoTracking()
            .Include(r => r.ReturnItems).ThenInclude(ri => ri.OriginalOrderItem)
            .Include(r => r.Refunds)
            .Include(r => r.OriginalOrder)
            .AsQueryable();

        if (branchId.HasValue)
            query = query.Where(r => r.BranchId == branchId.Value);

        if (outletId.HasValue)
        {
            int outId = outletId.Value;
            query = query.Where(r => (r.OriginalOrder != null && r.OriginalOrder.OutletId == outId)
                || context.Terminals.Any(t => t.TerminalId == r.TerminalId && t.OutletId == outId));
        }

        if (terminalId.HasValue)
            query = query.Where(r => r.TerminalId == terminalId.Value);

        var returns = await query
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync(cancellationToken);

        var branchesById = await context.Branches.AsNoTracking().ToDictionaryAsync(b => b.BranchId, cancellationToken);
        var outletsById = await context.Outlets.AsNoTracking().ToDictionaryAsync(o => o.OutletId, cancellationToken);
        var terminalsById = await context.Terminals.AsNoTracking().ToDictionaryAsync(t => t.TerminalId, cancellationToken);

        var dtos = returns.Select(r =>
        {
            int termId = r.TerminalId > 0 ? r.TerminalId : (r.OriginalOrder?.TerminalId ?? 0);
            terminalsById.TryGetValue(termId, out var term);
            int outId = term != null && term.OutletId > 0 ? term.OutletId : (r.OriginalOrder?.OutletId ?? 0);
            string outName = outletsById.TryGetValue(outId, out var outl)
                ? outl.Name
                : (outId > 0 ? $"Outlet #{outId}" : "Unknown / Legacy Outlet");
            int brId = r.BranchId > 0 ? r.BranchId : (r.OriginalOrder?.BranchId ?? (term?.BranchId ?? 0));
            string brName = branchesById.TryGetValue(brId, out var br)
                ? br.Name
                : (brId > 0 ? $"Branch #{brId}" : "Unknown Branch");
            string tCode = term?.TerminalCode ?? (termId > 0 ? $"LEGACY-{termId}" : "LEGACY");
            string tName = term?.Name ?? (termId > 0 ? $"Unknown / Legacy Terminal (#{termId})" : "Unknown / Legacy Terminal");
            string refMethod = r.Refunds.FirstOrDefault()?.RefundMethod ?? "CASH";

            return new ReturnResponseDto
            {
                ReturnId = r.ReturnId,
                OriginalOrderId = r.OriginalOrderId,
                BranchId = brId,
                BranchName = brName,
                OutletId = outId,
                OutletName = outName,
                TerminalId = termId,
                TerminalCode = tCode,
                TerminalName = tName,
                RefundMethod = refMethod,
                OriginalInvoiceNumber = r.OriginalOrder?.InvoiceNumber,
                ReturnReference = r.ReturnReference,
                ReturnReason = r.ReturnReason,
                Status = r.Status,
                RequestedBy = r.RequestedBy,
                ApprovedBy = r.ApprovedBy,
                TotalRefundAmount = r.TotalRefundAmount,
                CreatedAt = r.CreatedAt,
                ReturnItems = r.ReturnItems.Select(ri => new ReturnItemResponseDto
                {
                    ReturnItemId = ri.ReturnItemId,
                    OriginalOrderItemId = ri.OriginalOrderItemId,
                    ProductName = ri.OriginalOrderItem?.ProductName ?? $"Item #{ri.OriginalOrderItemId}",
                    Quantity = ri.Quantity,
                    OriginalUnitPrice = ri.OriginalUnitPrice,
                    RefundAmount = ri.RefundAmount,
                    RestockDisposition = ri.RestockDisposition
                }).ToList()
            };
        }).ToList();

        return Ok(dtos);
    }

    [HttpGet("{id:long}")]
    public async Task<ActionResult<ReturnResponseDto>> GetReturnById(long id, CancellationToken cancellationToken = default)
    {
        var r = await context.SalesReturns
            .AsNoTracking()
            .Include(r => r.ReturnItems)
            .SingleOrDefaultAsync(r => r.ReturnId == id, cancellationToken);

        if (r == null) return NotFound();

        var orderItems = await context.OrderItems
            .AsNoTracking()
            .Where(oi => r.ReturnItems.Select(ri => ri.OriginalOrderItemId).Contains(oi.OrderItemId))
            .ToListAsync(cancellationToken);

        return Ok(new ReturnResponseDto
        {
            ReturnId = r.ReturnId,
            OriginalOrderId = r.OriginalOrderId,
            ReturnReference = r.ReturnReference,
            ReturnReason = r.ReturnReason,
            Status = r.Status,
            RequestedBy = r.RequestedBy,
            ApprovedBy = r.ApprovedBy,
            TotalRefundAmount = r.TotalRefundAmount,
            CreatedAt = r.CreatedAt,
            ReturnItems = r.ReturnItems.Select(ri => {
                var oi = orderItems.FirstOrDefault(x => x.OrderItemId == ri.OriginalOrderItemId);
                return new ReturnItemResponseDto
                {
                    ReturnItemId = ri.ReturnItemId,
                    OriginalOrderItemId = ri.OriginalOrderItemId,
                    ProductName = oi?.ProductName ?? "Product",
                    Quantity = ri.Quantity,
                    OriginalUnitPrice = ri.OriginalUnitPrice,
                    RefundAmount = ri.RefundAmount,
                    RestockDisposition = ri.RestockDisposition
                };
            }).ToList()
        });
    }
    [HttpPost]
    public async Task<ActionResult<ReturnResponseDto>> ProcessReturn([FromBody] ReturnRequestDto dto, CancellationToken cancellationToken = default)
    {
        if (dto == null || dto.OriginalOrderId <= 0 || dto.Items == null || dto.Items.Count == 0)
            return BadRequest("Invalid return request details.");

        using var transaction = await context.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken);
        try
        {
            var order = await context.Orders
                .Include(o => o.OrderItems)
                .Include(o => o.Payments)
                .SingleOrDefaultAsync(o => o.OrderId == dto.OriginalOrderId, cancellationToken);

            if (order == null) return NotFound("Original order not found.");
            if (order.PaymentStatus != "PAID") return BadRequest("Only paid orders can be processed for returns/refunds.");

            // Verify supervisor authorization strictly by PIN code
            User? approver = null;
            if (!string.IsNullOrWhiteSpace(dto.ApprovedByPinOrUsername))
            {
                var cleanPin = dto.ApprovedByPinOrUsername.Trim();
                approver = await context.Users
                    .SingleOrDefaultAsync(u => u.PinCode == cleanPin, cancellationToken);
            }

            if (approver == null)
            {
                return BadRequest("Supervisor PIN not found or invalid.");
            }

            var approverRole = approver.Role?.Trim() ?? "";
            if (!approverRole.Equals("Admin", StringComparison.OrdinalIgnoreCase) &&
                !approverRole.Equals("Manager", StringComparison.OrdinalIgnoreCase) &&
                !approverRole.Equals("Senior Cashier", StringComparison.OrdinalIgnoreCase) &&
                !approverRole.Equals("SeniorCashier", StringComparison.OrdinalIgnoreCase))
            {
                return BadRequest($"User '{approver.Username}' has role '{approver.Role}', which does not have authorization to approve returns/refunds.");
            }

            decimal totalRefund = 0m;
            var returnRecord = new SalesReturn
            {
                OriginalOrderId = order.OrderId,
                BranchId = order.BranchId,
                TerminalId = order.TerminalId,
                ReturnReference = "RET-" + Guid.NewGuid().ToString().Substring(0, 8).ToUpper(),
                ReturnReason = dto.ReturnReason.Trim(),
                Status = "COMPLETED",
                RequestedBy = User.Identity?.Name ?? "system",
                ApprovedBy = approver.Username,
                CreatedAt = DateTime.UtcNow,
                CompletedAt = DateTime.UtcNow
            };

            context.SalesReturns.Add(returnRecord);
            await context.SaveChangesAsync(cancellationToken);

            var itemResponses = new List<ReturnItemResponseDto>();

            // Validate against previously returned quantities to prevent over-refunds
            var existingReturns = await context.SalesReturns
                .Where(r => r.OriginalOrderId == order.OrderId && r.Status == "COMPLETED")
                .Include(r => r.ReturnItems)
                .ToListAsync(cancellationToken);

            var alreadyReturnedQtys = existingReturns
                .SelectMany(r => r.ReturnItems)
                .GroupBy(ri => ri.OriginalOrderItemId)
                .ToDictionary(g => g.Key, g => g.Sum(ri => ri.Quantity));

            foreach (var itemReq in dto.Items)
            {
                var orderItem = order.OrderItems.FirstOrDefault(i => i.OrderItemId == itemReq.OriginalOrderItemId);
                if (orderItem == null) return BadRequest($"Order item {itemReq.OriginalOrderItemId} not found in original order.");

                alreadyReturnedQtys.TryGetValue(orderItem.OrderItemId, out decimal returnedQty);
                decimal remainingEligible = orderItem.Quantity - returnedQty;

                if (itemReq.Quantity <= 0 || itemReq.Quantity > remainingEligible)
                    return BadRequest($"Invalid return quantity for product {orderItem.ProductName}. Remaining eligible quantity is {remainingEligible}.");

                // Calculate prorated refund amount based on historical unit price and historical tax/discounts
                decimal unitRefundPrice = orderItem.LineTotal / orderItem.Quantity;
                decimal lineRefundAmount = Math.Round(unitRefundPrice * itemReq.Quantity, 2);
                totalRefund += lineRefundAmount;

                decimal allocatedTax = Math.Round((orderItem.VatAmount / orderItem.Quantity) * itemReq.Quantity, 2);
                decimal allocatedDiscount = Math.Round((orderItem.DiscountAmount / orderItem.Quantity) * itemReq.Quantity, 2);

                var returnItem = new SalesReturnItem
                {
                    ReturnId = returnRecord.ReturnId,
                    OriginalOrderItemId = orderItem.OrderItemId,
                    Quantity = itemReq.Quantity,
                    OriginalUnitPrice = orderItem.UnitPrice,
                    AllocatedDiscount = allocatedDiscount,
                    AllocatedTax = allocatedTax,
                    RefundAmount = lineRefundAmount,
                    RestockDisposition = itemReq.RestockDisposition
                };

                context.SalesReturnItems.Add(returnItem);

                // Handle inventory restock ONLY if disposition is RESTOCK (resellable goods)
                if (itemReq.RestockDisposition.Equals("RESTOCK", StringComparison.OrdinalIgnoreCase))
                {
                    var inventory = await context.BranchInventories
                        .FirstOrDefaultAsync(inv => inv.ProductId == orderItem.ProductId && inv.BranchId == order.BranchId, cancellationToken);
                    if (inventory != null)
                    {
                        inventory.StockQuantity += (int)Math.Round(itemReq.Quantity);
                        inventory.LastUpdated = DateTime.UtcNow;
                    }
                }

                itemResponses.Add(new ReturnItemResponseDto
                {
                    ReturnItemId = returnItem.ReturnItemId,
                    OriginalOrderItemId = orderItem.OrderItemId,
                    ProductName = orderItem.ProductName,
                    Quantity = itemReq.Quantity,
                    OriginalUnitPrice = orderItem.UnitPrice,
                    RefundAmount = lineRefundAmount,
                    RestockDisposition = returnItem.RestockDisposition
                });
            }

            returnRecord.TotalRefundAmount = totalRefund;

            // Link refund against primary successful payment
            var primaryPayment = order.Payments.FirstOrDefault(p => p.PaymentStatus == "SUCCESS");
            if (primaryPayment != null)
            {
                context.Refunds.Add(new Refund
                {
                    ReturnId = returnRecord.ReturnId,
                    OriginalPaymentId = primaryPayment.PaymentId,
                    RefundMethod = dto.RefundMethod,
                    RefundAmount = totalRefund,
                    ReferenceNumber = dto.RefundReferenceNumber,
                    Status = "COMPLETED",
                    RequestedAt = DateTime.UtcNow,
                    CompletedAt = DateTime.UtcNow
                });
            }

            if (totalRefund > 0m && string.Equals(dto.RefundMethod, "CASH", StringComparison.OrdinalIgnoreCase))
            {
                var openShift = await context.CashDrawerShifts
                    .Where(s => s.TerminalId == order.TerminalId && s.Status == "OPEN")
                    .OrderByDescending(s => s.OpenedAt)
                    .FirstOrDefaultAsync(cancellationToken);

                if (openShift != null)
                {
                    openShift.TotalCashRefunds += totalRefund;
                    CashDrawerController.RecalculateShiftExpectedBalance(openShift);
                }
            }

            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return Ok(new ReturnResponseDto
            {
                ReturnId = returnRecord.ReturnId,
                OriginalOrderId = order.OrderId,
                ReturnReference = returnRecord.ReturnReference,
                ReturnReason = returnRecord.ReturnReason,
                Status = returnRecord.Status,
                RequestedBy = returnRecord.RequestedBy,
                ApprovedBy = returnRecord.ApprovedBy,
                TotalRefundAmount = returnRecord.TotalRefundAmount,
                CreatedAt = returnRecord.CreatedAt,
                ReturnItems = itemResponses
            });
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync(cancellationToken);
            return StatusCode(500, "Return execution error: " + ex.Message);
        }
    }
}

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
using POS.Shared.Interfaces;

namespace POS.Api.Controllers;

[Route("api/[controller]")]
[ApiController]
[Authorize]
public class TransactionsController(PosDbContext context, IReceiptService receiptService) : ControllerBase
{
    private bool IsSupervisor() =>
        User.IsInRole("Admin") || User.IsInRole("Manager") || User.IsInRole("SeniorCashier");

    // Orders.CreatedAt is stored as UTC in a 'timestamp without time zone' column.
    private static DateTime ToUtcUnspecified(DateTime value) =>
        DateTime.SpecifyKind(value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : value, DateTimeKind.Unspecified);

    // Business day starts at 04:00 AM Philippine Time (UTC+8).
    private static DateTime GetBusinessDayCutoffUtc()
    {
        var nowPht = DateTime.UtcNow.AddHours(8);
        var businessDate = nowPht.TimeOfDay < TimeSpan.FromHours(4) ? nowPht.Date.AddDays(-1) : nowPht.Date;
        var businessDayStartPht = businessDate.AddHours(4);
        return businessDayStartPht.AddHours(-8);
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<OrderResponseDto>>> GetTransactions(
        [FromQuery] DateTime? dateFrom,
        [FromQuery] DateTime? dateTo,
        [FromQuery] int? branchId,
        [FromQuery] int? terminalId,
        [FromQuery] string? cashierId,
        [FromQuery] string? orderNumber,
        [FromQuery] string? invoiceNumber,
        [FromQuery] string? paymentMethod,
        [FromQuery] string? orderStatus,
        [FromQuery] string? paymentStatus,
        [FromQuery] string? discountCode,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default,
        [FromQuery] int? outletId = null)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var query = context.Orders.AsNoTracking()
            .Include(o => o.OrderItems).ThenInclude(i => i.OrderItemModifiers)
            .Include(o => o.Payments)
            .AsQueryable();

        // Non-Admin/Manager/SeniorCashier users (Ordinary Cashiers) can only view their own transactions
        if (!IsSupervisor())
        {
            var currentUsername = User.Identity?.Name;
            if (!string.IsNullOrWhiteSpace(currentUsername))
            {
                query = query.Where(o => o.CashierId == currentUsername);
            }
            else
            {
                return Forbid();
            }

            // Enforce branch and outlet scoping from token claims if present
            var branchClaim = User.FindFirst("branch_id")?.Value;
            var outletClaim = User.FindFirst("terminal_id")?.Value;
            if (string.IsNullOrWhiteSpace(outletClaim)) return Forbid();

            if (int.TryParse(branchClaim, out var bClaimId))
            {
                if (branchId.HasValue && branchId.Value != bClaimId) return Forbid();
                query = query.Where(o => o.BranchId == bClaimId);
            }
            else if (branchId.HasValue)
            {
                query = query.Where(o => o.BranchId == branchId.Value);
            }

            if (int.TryParse(outletClaim, out var oClaimId))
            {
                if (terminalId.HasValue && terminalId.Value != oClaimId) return Forbid();
                query = query.Where(o => o.TerminalId == oClaimId);
            }
            else if (terminalId.HasValue)
            {
                query = query.Where(o => o.TerminalId == terminalId.Value);
            }

            // Safety net: 4:00 AM cutoff of current business day (sales reset at 4am)
            var floor = ToUtcUnspecified(GetBusinessDayCutoffUtc());
            var requested = dateFrom.HasValue ? ToUtcUnspecified(dateFrom.Value) : floor;
            dateFrom = requested < floor ? floor : requested;
        }
        else
        {
            if (branchId.HasValue)
                query = query.Where(o => o.BranchId == branchId.Value);

            if (terminalId.HasValue)
                query = query.Where(o => o.TerminalId == terminalId.Value);

            if (!string.IsNullOrWhiteSpace(cashierId))
                query = query.Where(o => o.CashierId == cashierId);
        }

        if (outletId.HasValue)
            query = query.Where(o => context.Terminals.Any(t => t.TerminalId == o.TerminalId && t.OutletId == outletId));
        if (dateFrom.HasValue)
        {
            var from = ToUtcUnspecified(dateFrom.Value);
            query = query.Where(o => o.CreatedAt >= from);
        }

        if (dateTo.HasValue)
        {
            var to = ToUtcUnspecified(dateTo.Value);
            query = query.Where(o => o.CreatedAt <= to);
        }

        if (!string.IsNullOrWhiteSpace(orderNumber))
            query = query.Where(o => o.OrderNumber.Contains(orderNumber));

        if (!string.IsNullOrWhiteSpace(invoiceNumber))
            query = query.Where(o => o.InvoiceNumber != null && o.InvoiceNumber.Contains(invoiceNumber));

        if (!string.IsNullOrWhiteSpace(orderStatus))
            query = query.Where(o => o.OrderStatus == orderStatus);

        if (!string.IsNullOrWhiteSpace(paymentStatus))
            query = query.Where(o => o.PaymentStatus == paymentStatus);

        if (!string.IsNullOrWhiteSpace(discountCode))
            query = query.Where(o => o.DiscountCode == discountCode);

        if (!string.IsNullOrWhiteSpace(paymentMethod))
            query = query.Where(o => o.Payments.Any(p => p.PaymentMethod == paymentMethod));

        var orders = await query
            .OrderByDescending(o => o.CreatedAt)
            .ThenByDescending(o => o.OrderId)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        var result = orders.Select(o => new OrderResponseDto
        {
            OrderId = o.OrderId,
            OrderNumber = o.OrderNumber,
            BranchId = o.BranchId,
            TerminalId = o.TerminalId,
            CashierId = o.CashierId,
            ShiftId = o.ShiftId,
            OrderType = o.OrderType,
            OrderStatus = o.OrderStatus,
            PaymentStatus = o.PaymentStatus,
            Subtotal = o.Subtotal,
            DiscountAmount = o.DiscountAmount,
            TotalAmount = o.TotalAmount,
            VatableSales = o.VatableSales,
            VatExemptSales = o.VatExemptSales,
            ZeroRatedSales = o.ZeroRatedSales,
            VatAmount = o.VatAmount,
            DiscountCode = o.DiscountCode,
            CustomerCardNumber = o.CustomerCardNumber,
            InvoiceNumber = o.InvoiceNumber,
            InvoiceIssuedAt = o.InvoiceIssuedAt,
            CreatedAt = o.CreatedAt,
            CustomerName = o.CustomerName,
            CustomerAddress = o.CustomerAddress,
            CustomerTin = o.CustomerTin,
            CustomerBusinessStyle = o.CustomerBusinessStyle,
            Items = o.OrderItems.Select(i => new OrderItemResponseDto
            {
                OrderItemId = i.OrderItemId,
                ProductId = i.ProductId,
                ProductName = i.ProductName,
                ProductShortCode = i.ProductShortCode,
                Quantity = i.Quantity,
                UnitPrice = i.UnitPrice,
                LineTotal = i.LineTotal,
                VatType = i.VatType,
                VatAmount = i.VatAmount,
                Modifiers = i.OrderItemModifiers.Select(m => new OrderItemModifierResponseDto
                {
                    ModifierOptionId = m.ModifierOptionId,
                    ModifierName = m.ModifierName,
                    AdditionalPrice = m.AdditionalPrice
                }).ToList()
            }).ToList(),
            Payments = o.Payments.Select(p => new PaymentResponseDto
            {
                PaymentId = p.PaymentId,
                PaymentMethod = p.PaymentMethod,
                Amount = p.Amount,
                AmountTendered = p.AmountTendered,
                ChangeAmount = p.ChangeAmount,
                ReferenceNumber = p.ReferenceNumber,
                PaymentStatus = p.PaymentStatus,
                PaidAt = p.PaidAt
            }).ToList()
        }).ToList();

        return Ok(result);
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<OrderResponseDto>> GetTransactionById(long id, CancellationToken cancellationToken = default)
    {
        var receipt = await receiptService.GetReceiptAsync(id, false);
        if (receipt == null) return NotFound();

        // Map receipt to OrderResponseDto
        var order = await context.Orders.AsNoTracking()
            .Include(o => o.OrderItems).ThenInclude(i => i.OrderItemModifiers)
            .Include(o => o.Payments)
            .SingleOrDefaultAsync(o => o.OrderId == id, cancellationToken);

        if (order == null) return NotFound();

        if (!IsSupervisor())
        {
            if (string.IsNullOrWhiteSpace(User.Identity?.Name) ||
                !string.Equals(order.CashierId, User.Identity.Name, StringComparison.OrdinalIgnoreCase))
                return Forbid();

            var branchClaim = User.FindFirst("branch_id")?.Value;
            if (int.TryParse(branchClaim, out var bClaimId) && order.BranchId != bClaimId)
                return Forbid();

            var outletClaim = User.FindFirst("terminal_id")?.Value;
            if (string.IsNullOrWhiteSpace(outletClaim)) return Forbid();
            if (int.TryParse(outletClaim, out var oClaimId) && order.TerminalId != oClaimId)
                return Forbid();

            var floor = ToUtcUnspecified(GetBusinessDayCutoffUtc());
            if (order.CreatedAt < floor)
                return Forbid();
        }

        var dto = new OrderResponseDto
        {
            OrderId = order.OrderId,
            OrderNumber = order.OrderNumber,
            BranchId = order.BranchId,
            TerminalId = order.TerminalId,
            CashierId = order.CashierId,
            ShiftId = order.ShiftId,
            OrderType = order.OrderType,
            OrderStatus = order.OrderStatus,
            PaymentStatus = order.PaymentStatus,
            Subtotal = order.Subtotal,
            DiscountAmount = order.DiscountAmount,
            TotalAmount = order.TotalAmount,
            VatableSales = order.VatableSales,
            VatExemptSales = order.VatExemptSales,
            ZeroRatedSales = order.ZeroRatedSales,
            VatAmount = order.VatAmount,
            DiscountCode = order.DiscountCode,
            CustomerCardNumber = order.CustomerCardNumber,
            InvoiceNumber = order.InvoiceNumber,
            InvoiceIssuedAt = order.InvoiceIssuedAt,
            CreatedAt = order.CreatedAt,
            CustomerName = order.CustomerName,
            CustomerAddress = order.CustomerAddress,
            CustomerTin = order.CustomerTin,
            CustomerBusinessStyle = order.CustomerBusinessStyle,
            Items = order.OrderItems.Select(i => new OrderItemResponseDto
            {
                OrderItemId = i.OrderItemId,
                ProductId = i.ProductId,
                ProductName = i.ProductName,
                ProductShortCode = i.ProductShortCode,
                Quantity = i.Quantity,
                UnitPrice = i.UnitPrice,
                LineTotal = i.LineTotal,
                VatType = i.VatType,
                VatAmount = i.VatAmount,
                Modifiers = i.OrderItemModifiers.Select(m => new OrderItemModifierResponseDto
                {
                    ModifierOptionId = m.ModifierOptionId,
                    ModifierName = m.ModifierName,
                    AdditionalPrice = m.AdditionalPrice
                }).ToList()
            }).ToList(),
            Payments = order.Payments.Select(p => new PaymentResponseDto
            {
                PaymentId = p.PaymentId,
                PaymentMethod = p.PaymentMethod,
                Amount = p.Amount,
                AmountTendered = p.AmountTendered,
                ChangeAmount = p.ChangeAmount,
                ReferenceNumber = p.ReferenceNumber,
                PaymentStatus = p.PaymentStatus,
                PaidAt = p.PaidAt
            }).ToList()
        };

        return Ok(dto);
    }

    [HttpGet("{id}/receipt")]
    public async Task<ActionResult<ReceiptDto>> GetTransactionReceipt(long id, [FromQuery] bool reprint = false, CancellationToken cancellationToken = default)
    {
        // If reprinting, require Admin, Manager, or SeniorCashier authorization
        if (reprint && !User.IsInRole("Admin") && !User.IsInRole("Manager") && !User.IsInRole("SeniorCashier"))
        {
            return Forbid();
        }

        var owner = await context.Orders.AsNoTracking()
            .Where(o => o.OrderId == id).Select(o => new { o.CashierId, o.BranchId, o.TerminalId }).SingleOrDefaultAsync(cancellationToken);
        if (owner == null) return NotFound();

        if (!IsSupervisor())
        {
            if (string.IsNullOrWhiteSpace(User.Identity?.Name) ||
                !string.Equals(owner.CashierId, User.Identity.Name, StringComparison.OrdinalIgnoreCase))
                return Forbid();

            var branchClaim = User.FindFirst("branch_id")?.Value;
            if (int.TryParse(branchClaim, out var bClaimId) && owner.BranchId != bClaimId)
                return Forbid();

            var outletClaim = User.FindFirst("terminal_id")?.Value;
            if (string.IsNullOrWhiteSpace(outletClaim)) return Forbid();
            if (int.TryParse(outletClaim, out var oClaimId) && owner.TerminalId != oClaimId)
                return Forbid();
        }

        var receipt = await receiptService.GetReceiptAsync(id, reprint);
        if (receipt == null) return NotFound();

        if (reprint)
        {
            try
            {
                var order = await context.Orders.AsNoTracking().SingleOrDefaultAsync(o => o.OrderId == id, cancellationToken);
                if (order != null)
                {
                    // Use a new context instance or direct raw insert to avoid tracking conflicts on audit logging
                    await context.Database.ExecuteSqlRawAsync(
                        "INSERT INTO \"ReceiptReprintLogs\" (\"OrderId\", \"InvoiceNumber\", \"BranchId\", \"TerminalId\", \"CashierId\", \"ReprintReason\", \"ReprintedAt\", \"PrinterName\", \"PrintStatus\") VALUES ({0}, {1}, {2}, {3}, {4}, {5}, {6}, {7}, {8})",
                        [order.OrderId, (object?)order.InvoiceNumber ?? DBNull.Value, order.BranchId, order.TerminalId, order.CashierId, "CONTROLLED_REPRINT", DateTime.UtcNow, "SYSTEM_DEFAULT_PRINTER", "SUCCESS"],
                        cancellationToken);
                }
            }
            catch (Exception ex)
            {
                // Non-blocking audit log catch so receipt reprint is never disrupted by db audit constraints
                Console.WriteLine("Warning: Failed to persist reprint audit log: " + ex.Message);
            }
        }

        return Ok(receipt);
    }

    [HttpPost("{id:long}/void")]
    public async Task<ActionResult<VoidResponseDto>> VoidSpecificTransaction(long id, [FromBody] VoidRequestDto dto, CancellationToken cancellationToken = default)
    {
        if (dto == null || string.IsNullOrWhiteSpace(dto.VoidReason))
            return BadRequest("Void reason is required.");

        dto.OriginalOrderId = id;
        var returnsController = new ReturnsController(context);
        returnsController.ControllerContext = ControllerContext;
        return await returnsController.VoidTransaction(dto, cancellationToken);
    }

    [HttpGet("{id:long}/void")]
    public async Task<ActionResult<VoidResponseDto>> GetTransactionVoid(long id, CancellationToken cancellationToken = default)
    {
        var voidRecord = await context.TransactionVoids
            .AsNoTracking()
            .SingleOrDefaultAsync(v => v.OriginalOrderId == id, cancellationToken);

        if (voidRecord == null) return NotFound("No void record found for this transaction.");

        return Ok(new VoidResponseDto
        {
            VoidId = voidRecord.VoidId,
            OriginalOrderId = voidRecord.OriginalOrderId,
            OriginalInvoiceNumber = voidRecord.OriginalInvoiceNumber,
            VoidReason = voidRecord.VoidReason,
            RequestedBy = voidRecord.RequestedBy,
            ApprovedBy = voidRecord.ApprovedBy,
            VoidedAt = voidRecord.VoidedAt,
            Status = voidRecord.Status,
            ReversalReference = voidRecord.ReversalReference
        });
    }

    [HttpGet("summary")]
    public async Task<ActionResult<TransactionSummaryDto>> GetTransactionSummary(
        [FromQuery] DateTime? dateFrom,
        [FromQuery] DateTime? dateTo,
        [FromQuery] int? branchId,
        [FromQuery] int? terminalId,
        [FromQuery] string? cashierId,
        CancellationToken cancellationToken = default,
        [FromQuery] int? outletId = null)
    {
        var query = context.Orders.AsNoTracking().Where(o => o.PaymentStatus == "PAID");
        if (outletId.HasValue)
            query = query.Where(o => context.Terminals.Any(t => t.TerminalId == o.TerminalId && t.OutletId == outletId));

        if (!IsSupervisor())
        {
            var currentUsername = User.Identity?.Name;
            if (!string.IsNullOrWhiteSpace(currentUsername))
            {
                query = query.Where(o => o.CashierId == currentUsername);
            }
            else
            {
                return Forbid();
            }

            var branchClaim = User.FindFirst("branch_id")?.Value;
            var outletClaim = User.FindFirst("terminal_id")?.Value;
            if (string.IsNullOrWhiteSpace(outletClaim)) return Forbid();

            if (int.TryParse(branchClaim, out var bClaimId))
            {
                if (branchId.HasValue && branchId.Value != bClaimId) return Forbid();
                query = query.Where(o => o.BranchId == bClaimId);
            }
            else if (branchId.HasValue)
            {
                query = query.Where(o => o.BranchId == branchId.Value);
            }

            if (int.TryParse(outletClaim, out var oClaimId))
            {
                if (terminalId.HasValue && terminalId.Value != oClaimId) return Forbid();
                query = query.Where(o => o.TerminalId == oClaimId);
            }
            else if (terminalId.HasValue)
            {
                query = query.Where(o => o.TerminalId == terminalId.Value);
            }

            var floor = ToUtcUnspecified(GetBusinessDayCutoffUtc());
            var requested = dateFrom.HasValue ? ToUtcUnspecified(dateFrom.Value) : floor;
            dateFrom = requested < floor ? floor : requested;
        }
        else
        {
            if (branchId.HasValue)
                query = query.Where(o => o.BranchId == branchId.Value);

            if (terminalId.HasValue)
                query = query.Where(o => o.TerminalId == terminalId.Value);

            if (!string.IsNullOrWhiteSpace(cashierId))
                query = query.Where(o => o.CashierId == cashierId);
        }

        if (dateFrom.HasValue)
            query = query.Where(o => o.CreatedAt >= ToUtcUnspecified(dateFrom.Value));

        if (dateTo.HasValue)
            query = query.Where(o => o.CreatedAt <= ToUtcUnspecified(dateTo.Value));

        var count = await query.CountAsync(cancellationToken);
        var grossSales = await query.SumAsync(o => (decimal?)o.Subtotal, cancellationToken) ?? 0m;
        var totalDiscounts = await query.SumAsync(o => (decimal?)o.DiscountAmount, cancellationToken) ?? 0m;
        var netSales = await query.SumAsync(o => (decimal?)o.TotalAmount, cancellationToken) ?? 0m;
        var vatableSales = await query.SumAsync(o => (decimal?)o.VatableSales, cancellationToken) ?? 0m;
        var vatExemptSales = await query.SumAsync(o => (decimal?)o.VatExemptSales, cancellationToken) ?? 0m;
        var zeroRatedSales = await query.SumAsync(o => (decimal?)o.ZeroRatedSales, cancellationToken) ?? 0m;
        var totalVat = await query.SumAsync(o => (decimal?)o.VatAmount, cancellationToken) ?? 0m;

        return Ok(new TransactionSummaryDto
        {
            TotalTransactions = count,
            GrossSales = grossSales,
            TotalDiscounts = totalDiscounts,
            NetSales = netSales,
            VatableSales = vatableSales,
            VatExemptSales = vatExemptSales,
            ZeroRatedSales = zeroRatedSales,
            TotalVat = totalVat
        });
    }
}

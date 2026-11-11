using Microsoft.EntityFrameworkCore;
using POS.Api.Models;
using POS.Shared.Models;

namespace POS.Api.Services;

public partial class ZReadingService
{
    private async Task AggregateAsync(ZReadingReportDto report, CancellationToken ct)
    {
        var terminal = await db.Terminals.AsNoTracking().Include(x => x.Branch).Include(x => x.Outlet).SingleAsync(x => x.TerminalId == report.TerminalId, ct);
        report.BranchName = terminal.Branch.Name;
        report.BusinessName = terminal.Branch.Name;
        report.Address = terminal.Branch.Address;
        report.OutletName = terminal.Outlet.Name;
        report.MachineIdentificationNumber = terminal.Minnumber;
        report.PermitToUseNumber = terminal.Ptunumber;
        report.SerialNumber = terminal.SerialNumber;
        var start = report.PeriodStartUtc; var end = report.PeriodEndUtc;
        // Invoice issuance is immutable sale attribution; current VOIDED/REFUNDED flags do not erase collections.
        var sales = await db.Orders.AsNoTracking().Include(x => x.OrderItems).Include(x => x.Payments)
            .Where(x => x.TerminalId == report.TerminalId && x.InvoiceIssuedAt >= start && x.InvoiceIssuedAt < end).ToListAsync(ct);
        var returns = await db.SalesReturns.AsNoTracking().Include(x => x.OriginalOrder).Include(x => x.ReturnItems).ThenInclude(x => x.OriginalOrderItem)
            .Where(x => x.TerminalId == report.TerminalId && x.Status == "COMPLETED" && x.CompletedAt >= start && x.CompletedAt < end).ToListAsync(ct);
        var voids = await db.TransactionVoids.AsNoTracking().Include(x => x.OriginalOrder)!.ThenInclude(x => x!.OrderItems)
            .Where(x => x.OriginalOrder != null && x.OriginalOrder.TerminalId == report.TerminalId && (x.Status == "APPROVED" || x.Status == "COMPLETED") && x.VoidedAt >= start && x.VoidedAt < end).ToListAsync(ct);
        var refunds = await db.Refunds.AsNoTracking().Include(x => x.OriginalPayment)
            .Where(x => x.OriginalPayment != null && x.OriginalPayment.Order.TerminalId == report.TerminalId && x.Status == "COMPLETED" && x.CompletedAt >= start && x.CompletedAt < end).ToListAsync(ct);
        var orderedRefunds = refunds.OrderBy(x => x.CompletedAt).ThenBy(x => x.RefundId).ToList();
        report.FirstRefundId = orderedRefunds.FirstOrDefault()?.RefundId;
        report.LastRefundId = orderedRefunds.LastOrDefault()?.RefundId;
        // A void without an issued invoice did not create revenue, so it cannot reverse revenue.
        var fiscalVoids = voids.Where(x => x.OriginalOrder!.InvoiceIssuedAt.HasValue).ToList();
        var returnOrderIds = returns.Select(x => x.OriginalOrderId).ToArray();
        var priorReturns = await db.SalesReturns.AsNoTracking().Where(x => fiscalVoids.Select(v => v.OriginalOrderId).Contains(x.OriginalOrderId) && x.Status == "COMPLETED" && x.CompletedAt < end).ToListAsync(ct);
        var cashier = new Dictionary<string, ZReadingCashierDto>(StringComparer.Ordinal);
        ZReadingCashierDto Cashier(string name)
        {
            if (!cashier.TryGetValue(name, out var value)) cashier[name] = value = new() { CashierId = name, CashierName = name };
            return value;
        }
        report.GrossSales = sales.Sum(x => x.Subtotal);
        report.Discounts = sales.Sum(x => x.DiscountAmount);
        report.VatableSales = sales.Sum(x => x.VatableSales);
        report.VatExemptSales = sales.Sum(x => x.VatExemptSales);
        report.ZeroRatedSales = sales.Sum(x => x.ZeroRatedSales);
        report.VatAmount = sales.Sum(x => x.VatAmount);
        // Statutory VAT removal and legacy rounding are explicit, never disguised as a second discount.
        report.VatAdjustment = sales.Sum(x => x.Subtotal - x.DiscountAmount - x.TotalAmount);
        report.Returns = returns.Sum(x => x.TotalRefundAmount);
        report.ReturnVatAmount = returns.SelectMany(x => x.ReturnItems).Sum(x => x.AllocatedTax);
        report.SaleCount = sales.Count; report.ReturnCount = returns.Count;
        report.VoidCount = voids.Count;
        var ordered = sales.OrderBy(x => x.InvoiceIssuedAt).ThenBy(x => x.OrderId).ToList();
        report.FirstInvoiceNumber = ordered.FirstOrDefault()?.InvoiceNumber;
        report.LastInvoiceNumber = ordered.LastOrDefault()?.InvoiceNumber;
        foreach (var sale in sales)
        {
            var c = Cashier(sale.CashierId); c.GrossSales += sale.Subtotal; c.Discounts += sale.DiscountAmount; c.NetSales += sale.TotalAmount; c.SaleCount++; c.ItemQuantity += sale.OrderItems.Sum(x => x.Quantity);
        }
        foreach (var ret in returns)
        {
            var c = Cashier(ret.RequestedBy); c.Returns += ret.TotalRefundAmount; c.NetSales -= ret.TotalRefundAmount; c.ReturnCount++; c.ReturnQuantity += ret.ReturnItems.Sum(x => x.Quantity);
            report.Adjustments.Add(new() { Kind = "RETURN", EventId = ret.ReturnId, OriginalOrderId = ret.OriginalOrderId, OriginalInvoiceNumber = ret.OriginalOrder?.InvoiceNumber, EventAtUtc = ret.CompletedAt!.Value, ProcessedBy = ret.RequestedBy, Amount = ret.TotalRefundAmount });
        }
        foreach (var v in voids)
        {
            // Returns already recognized before this void must not be subtracted twice.
            var prior = priorReturns.Where(x => x.OriginalOrderId == v.OriginalOrderId && x.CompletedAt <= v.VoidedAt).ToList();
            var amount = v.OriginalOrder!.InvoiceIssuedAt.HasValue ? Math.Max(0, v.OriginalOrder.TotalAmount - prior.Sum(x => x.TotalRefundAmount)) : 0m;
            var vat = v.OriginalOrder.InvoiceIssuedAt.HasValue ? Math.Max(0, v.OriginalOrder.VatAmount - prior.Sum(x => x.TotalRefundAmount == 0 ? 0 : x.TotalRefundAmount / Math.Max(.01m, v.OriginalOrder.TotalAmount) * v.OriginalOrder.VatAmount)) : 0m;
            report.Voids += amount; report.VoidVatAmount += decimal.Round(vat, 2, MidpointRounding.AwayFromZero);
            var c = Cashier(v.RequestedBy); c.Voids += amount; c.NetSales -= amount; c.VoidCount++; c.VoidQuantity += v.OriginalOrder.OrderItems.Sum(x => x.Quantity);
            report.Adjustments.Add(new() { Kind = "VOID", EventId = v.VoidId, OriginalOrderId = v.OriginalOrderId, OriginalInvoiceNumber = v.OriginalOrder.InvoiceNumber, EventAtUtc = v.VoidedAt, ProcessedBy = v.RequestedBy, Amount = amount });
        }
        report.NetSales = sales.Sum(x => x.TotalAmount) - report.Returns - report.Voids;
        report.NetVatAmount = report.VatAmount - report.ReturnVatAmount - report.VoidVatAmount;
        report.Cashiers = cashier.Values.OrderBy(x => x.CashierId, StringComparer.Ordinal).ToList();
        static List<ZReadingProductDto> Group(IEnumerable<ZReadingProductDto> source) => source
            .GroupBy(x => new { x.ProductId, x.ProductName, x.ProductShortCode, x.VatType })
            .Select(g => new ZReadingProductDto { ProductId = g.Key.ProductId, ProductName = g.Key.ProductName, ProductShortCode = g.Key.ProductShortCode, VatType = g.Key.VatType, Quantity = g.Sum(x => x.Quantity), Value = g.Sum(x => x.Value), Discounts = g.Sum(x => x.Discounts), VatAmount = g.Sum(x => x.VatAmount) })
            .OrderBy(x => x.ProductName, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.ProductId).ToList();
        report.Products = Group(sales.SelectMany(x => x.OrderItems).Select(x => new ZReadingProductDto { ProductId = x.ProductId, ProductName = x.ProductName, ProductShortCode = x.ProductShortCode, VatType = x.VatType, Quantity = x.Quantity, Value = x.LineTotal, Discounts = x.DiscountAmount, VatAmount = x.VatAmount }));
        report.ExemptProducts = report.Products.Where(x => x.VatType.Contains("EXEMPT", StringComparison.OrdinalIgnoreCase)).ToList();
        report.ReturnProducts = Group(returns.SelectMany(x => x.ReturnItems).Select(x => new ZReadingProductDto { ProductId = x.OriginalOrderItem?.ProductId ?? 0, ProductName = x.OriginalOrderItem?.ProductName ?? $"Historical item #{x.OriginalOrderItemId}", ProductShortCode = x.OriginalOrderItem?.ProductShortCode, VatType = x.OriginalOrderItem?.VatType ?? "UNKNOWN", Quantity = x.Quantity, Value = x.RefundAmount, Discounts = x.AllocatedDiscount, VatAmount = x.AllocatedTax }));
        report.VoidProducts = Group(voids.SelectMany(x => x.OriginalOrder!.OrderItems).Select(x => new ZReadingProductDto { ProductId = x.ProductId, ProductName = x.ProductName, ProductShortCode = x.ProductShortCode, VatType = x.VatType, Quantity = x.Quantity, Value = x.LineTotal, Discounts = x.DiscountAmount, VatAmount = x.VatAmount }));
        var payments = await db.Payments.AsNoTracking().Where(x => x.Order.TerminalId == report.TerminalId && x.PaidAt >= start && x.PaidAt < end && (x.PaymentStatus == POS.Shared.Constants.PaymentStatuses.Paid || x.PaymentStatus == "REFUNDED")).ToListAsync(ct);
        report.Payments = payments.Select(x => x.PaymentMethod).Concat(refunds.Select(x => x.RefundMethod)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .Select(method => { var p = payments.Where(x => string.Equals(x.PaymentMethod, method, StringComparison.OrdinalIgnoreCase)).ToList(); var r = refunds.Where(x => string.Equals(x.RefundMethod, method, StringComparison.OrdinalIgnoreCase)).ToList(); return new ZReadingPaymentDto { PaymentMethod = method, PaymentCount = p.Count, Collections = p.Sum(x => x.Amount), RefundCount = r.Count, Refunds = r.Sum(x => x.RefundAmount), NetCollections = p.Sum(x => x.Amount) - r.Sum(x => x.RefundAmount) }; }).ToList();
        report.DiscountTotals = sales.Where(x => x.DiscountAmount != 0).GroupBy(x => new { Code = x.DiscountCode ?? "UNSPECIFIED", Name = x.DiscountName ?? "Historical discount" }).OrderBy(x => x.Key.Code)
            .Select(g => new ZReadingDiscountDto { DiscountCode = g.Key.Code, DiscountName = g.Key.Name, Count = g.Count(), Amount = g.Sum(x => x.DiscountAmount) }).ToList();
        var shifts = await db.CashDrawerShifts.AsNoTracking().Where(x => x.TerminalId == report.TerminalId && x.OpenedAt >= start && x.OpenedAt < end).OrderBy(x => x.OpenedAt).ToListAsync(ct);
        report.Shifts = shifts.Select(x => new ZReadingShiftDto { ShiftId = x.ShiftId, CashierId = x.CashierId, OpenedAt = x.OpenedAt, ClosedAt = x.ClosedAt, Status = x.Status, OpeningFloat = x.OpeningFloat, CashSales = x.TotalCashSales, CashRefunds = x.TotalCashRefunds, CashIn = x.TotalCashIn, CashOut = x.TotalCashOut, CashDrops = x.TotalCashDrops, ExpectedBalance = x.ExpectedBalance, ActualCountedBalance = x.ActualCountedBalance, Discrepancy = x.Discrepancy }).ToList();
        report.HistoricalItemVatPrecisionAvailable = false;
        report.Warnings.Add("Historical item VAT precision/provenance is unavailable: item figures are stored values, not recomputed using current product taxes. Order VAT totals are authoritative; allocated adjustment VAT may be approximate.");
        report.Warnings.Add("Internal Z number is not a BIR accreditation or reset counter. Opening accumulated gross is operator-provisioned; drawer floats are separate per shift, not revenue.");
        if (report.Products.Sum(x => x.Value) != report.GrossSales) report.Warnings.Add("Historical product gross differs from order subtotal; order totals remain authoritative.");
    }
}

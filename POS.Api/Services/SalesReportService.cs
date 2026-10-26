using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using POS.Api.Models;
using POS.Shared.Interfaces;
using POS.Shared.Models;

namespace POS.Api.Services;

public class SalesReportService(PosDbContext context) : ISalesReportService
{
    public async Task<SalesReportResponseDto> GenerateReportAsync(SalesReportRequestDto request, CancellationToken cancellationToken = default)
    {
        var query = context.Orders.AsNoTracking()
            .AsSingleQuery()
            .Include(o => o.Payments)
            .Where(o => o.OrderStatus != "CANCELLED");

        if (request.DateFrom.HasValue)
            query = query.Where(o => o.CreatedAt >= request.DateFrom.Value);

        if (request.DateTo.HasValue)
            query = query.Where(o => o.CreatedAt <= request.DateTo.Value);

        if (request.BranchId.HasValue)
            query = query.Where(o => o.BranchId == request.BranchId.Value);

        if (request.OutletId.HasValue)
        {
            int outletIdFilter = request.OutletId.Value;
            query = query.Where(o => o.OutletId == outletIdFilter
                || context.Terminals.Any(t => t.TerminalId == o.TerminalId && t.OutletId == outletIdFilter));
        }

        if (request.TerminalId.HasValue)
            query = query.Where(o => o.TerminalId == request.TerminalId.Value);

        if (!string.IsNullOrWhiteSpace(request.CashierId))
            query = query.Where(o => o.CashierId == request.CashierId);

        if (!string.IsNullOrWhiteSpace(request.PaymentMethod))
            query = query.Where(o => o.Payments.Any(p => p.PaymentMethod == request.PaymentMethod));

        var allMatchingOrders = await query.ToListAsync(cancellationToken);

        var paidOrders = allMatchingOrders
            .Where(o => o.PaymentStatus == "PAID" && o.OrderStatus != "VOIDED")
            .ToList();

        var voidedOrders = allMatchingOrders
            .Where(o => o.PaymentStatus == "VOIDED" || o.OrderStatus == "VOIDED")
            .ToList();

        var branchesById = await context.Branches.AsNoTracking()
            .ToDictionaryAsync(b => b.BranchId, cancellationToken);
        var outletsById = await context.Outlets.AsNoTracking()
            .ToDictionaryAsync(o => o.OutletId, cancellationToken);
        var terminalsById = await context.Terminals.AsNoTracking()
            .ToDictionaryAsync(t => t.TerminalId, cancellationToken);
        var paymentMethodsByCode = (await context.PaymentMethods.AsNoTracking().ToListAsync(cancellationToken))
            .GroupBy(pm => pm.Code, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var paidOrderIds = paidOrders.Select(o => o.OrderId).ToList();
        var returnRefunds = paidOrderIds.Count > 0
            ? await context.Refunds.AsNoTracking()
                .Include(r => r.OriginalPayment)
                .Where(r => r.VoidId == null && r.Status == "COMPLETED" && r.OriginalPayment != null && paidOrderIds.Contains(r.OriginalPayment.OrderId))
                .ToListAsync(cancellationToken)
            : new List<Refund>();

        int returnTxCount = paidOrderIds.Count > 0
            ? await context.SalesReturns.AsNoTracking()
                .CountAsync(sr => paidOrderIds.Contains(sr.OriginalOrderId) && sr.Status == "COMPLETED", cancellationToken)
            : 0;

        var refundByOrderId = returnRefunds
            .Where(r => r.OriginalPayment != null)
            .GroupBy(r => r.OriginalPayment!.OrderId)
            .ToDictionary(g => g.Key, g => Math.Round(g.Sum(r => r.RefundAmount), 2, MidpointRounding.AwayFromZero));

        decimal totalRefunds = Math.Round(returnRefunds.Sum(r => r.RefundAmount), 2, MidpointRounding.AwayFromZero);

        var validPayments = paidOrders
            .SelectMany(o => o.Payments)
            .Where(p => p.PaymentStatus == "SUCCESS"
                && (string.IsNullOrWhiteSpace(request.PaymentMethod)
                    || string.Equals(p.PaymentMethod, request.PaymentMethod, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        decimal cashGross = validPayments
            .Where(p => string.Equals(p.PaymentMethod, "CASH", StringComparison.OrdinalIgnoreCase))
            .Sum(p => p.Amount);
        decimal cashRefunds = returnRefunds
            .Where(r => string.Equals(r.RefundMethod, "CASH", StringComparison.OrdinalIgnoreCase))
            .Sum(r => r.RefundAmount);

        decimal nonCashGross = validPayments
            .Where(p => !string.Equals(p.PaymentMethod, "CASH", StringComparison.OrdinalIgnoreCase))
            .Sum(p => p.Amount);
        decimal nonCashRefunds = returnRefunds
            .Where(r => !string.Equals(r.RefundMethod, "CASH", StringComparison.OrdinalIgnoreCase))
            .Sum(r => r.RefundAmount);

        var response = new SalesReportResponseDto
        {
            GeneratedAt = DateTime.UtcNow,
            Parameters = request,
            CompletedTransactionsCount = paidOrders.Count,
            VoidedTransactionsCount = voidedOrders.Count,
            VoidedAmount = Math.Round(voidedOrders.Sum(o => o.TotalAmount), 2, MidpointRounding.AwayFromZero),
            ReturnTransactionsCount = returnTxCount,
            GrossSales = Math.Round(paidOrders.Sum(o => o.Subtotal), 2, MidpointRounding.AwayFromZero),
            TotalDiscounts = Math.Round(paidOrders.Sum(o => o.DiscountAmount), 2, MidpointRounding.AwayFromZero),
            TotalRefunds = totalRefunds,
            NetSales = Math.Round(paidOrders.Sum(o => o.TotalAmount), 2, MidpointRounding.AwayFromZero),
            VatableSales = Math.Round(paidOrders.Sum(o => o.VatableSales), 2, MidpointRounding.AwayFromZero),
            VatExemptSales = Math.Round(paidOrders.Sum(o => o.VatExemptSales), 2, MidpointRounding.AwayFromZero),
            ZeroRatedSales = Math.Round(paidOrders.Sum(o => o.ZeroRatedSales), 2, MidpointRounding.AwayFromZero),
            OutputVat = Math.Round(paidOrders.Sum(o => o.VatAmount), 2, MidpointRounding.AwayFromZero),
            TotalPaymentsCollected = Math.Round(validPayments.Sum(p => p.Amount), 2, MidpointRounding.AwayFromZero),
            CashSalesCollected = Math.Round(cashGross - cashRefunds, 2, MidpointRounding.AwayFromZero),
            NonCashSalesCollected = Math.Round(nonCashGross - nonCashRefunds, 2, MidpointRounding.AwayFromZero)
        };

        response.AverageTransactionValue = response.CompletedTransactionsCount > 0
            ? Math.Round(response.NetSales / response.CompletedTransactionsCount, 2, MidpointRounding.AwayFromZero)
            : 0m;

        (int BranchId, string BranchName, int OutletId, string OutletName, int TerminalId, string TerminalCode, string TerminalName, bool IsLegacy) ResolveAttribution(Order o)
        {
            if (o.TerminalId > 0 && terminalsById.TryGetValue(o.TerminalId, out var term))
            {
                int outId = term.OutletId > 0 ? term.OutletId : o.OutletId;
                string outName = outletsById.TryGetValue(outId, out var outl)
                    ? outl.Name
                    : (outId > 0 ? $"Outlet #{outId}" : "Unknown / Legacy Outlet");
                int brId = o.BranchId > 0 ? o.BranchId : term.BranchId;
                string brName = branchesById.TryGetValue(brId, out var br)
                    ? br.Name
                    : (brId > 0 ? $"Branch #{brId}" : "Unknown Branch");
                return (brId, brName, outId, outName, term.TerminalId, term.TerminalCode, term.Name, false);
            }
            else
            {
                int outId = o.OutletId;
                string outName = outletsById.TryGetValue(outId, out var outl)
                    ? outl.Name
                    : (outId > 0 ? $"Outlet #{outId}" : "Unknown / Legacy Outlet");
                int brId = o.BranchId;
                string brName = branchesById.TryGetValue(brId, out var br)
                    ? br.Name
                    : (brId > 0 ? $"Branch #{brId}" : "Unknown Branch");
                string tCode = o.TerminalId > 0 ? $"LEGACY-{o.TerminalId}" : "LEGACY";
                string tName = o.TerminalId > 0 ? $"Unknown / Legacy Terminal (#{o.TerminalId})" : "Unknown / Legacy Terminal";
                return (brId, brName, outId, outName, o.TerminalId, tCode, tName, true);
            }
        }

        // Build Payment Method Breakdown (without multiplying order rows)
        var paymentCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in validPayments)
        {
            if (!string.IsNullOrWhiteSpace(p.PaymentMethod))
                paymentCodes.Add(p.PaymentMethod.Trim().ToUpperInvariant());
        }
        foreach (var r in returnRefunds)
        {
            if (!string.IsNullOrWhiteSpace(r.RefundMethod)
                && (string.IsNullOrWhiteSpace(request.PaymentMethod)
                    || string.Equals(r.RefundMethod, request.PaymentMethod, StringComparison.OrdinalIgnoreCase)))
            {
                paymentCodes.Add(r.RefundMethod.Trim().ToUpperInvariant());
            }
        }

        response.PaymentBreakdown = paymentCodes
            .Select(code =>
            {
                paymentMethodsByCode.TryGetValue(code, out var pm);
                var matchingPayments = validPayments
                    .Where(p => string.Equals(p.PaymentMethod?.Trim(), code, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                var matchingRefunds = returnRefunds
                    .Where(r => string.Equals(r.RefundMethod?.Trim(), code, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                bool isCash = string.Equals(code, "CASH", StringComparison.OrdinalIgnoreCase);
                return new SalesReportPaymentBreakdownDto
                {
                    PaymentMethodCode = pm?.Code ?? code,
                    PaymentMethodName = isCash ? "Cash" : (pm?.Name ?? code),
                    IsCash = isCash,
                    PaymentCount = matchingPayments.Count,
                    GrossCollected = Math.Round(matchingPayments.Sum(p => p.Amount), 2, MidpointRounding.AwayFromZero),
                    RefundedAmount = Math.Round(matchingRefunds.Sum(r => r.RefundAmount), 2, MidpointRounding.AwayFromZero)
                };
            })
            .OrderBy(p => p.IsCash ? 0 : 1)
            .ThenByDescending(p => p.NetCollected)
            .ToList();

        // Build Terminal Breakdown (including any voided or legacy-terminal orders)
        response.TerminalBreakdown = allMatchingOrders
            .Select(o => new { Order = o, Attr = ResolveAttribution(o) })
            .GroupBy(x => (x.Attr.BranchId, x.Attr.OutletId, x.Attr.TerminalId))
            .Select(g =>
            {
                var firstAttr = g.First().Attr;
                var groupPaid = g.Where(x => x.Order.PaymentStatus == "PAID" && x.Order.OrderStatus != "VOIDED").Select(x => x.Order).ToList();
                var groupVoided = g.Where(x => x.Order.PaymentStatus == "VOIDED" || x.Order.OrderStatus == "VOIDED").Select(x => x.Order).ToList();
                decimal groupRefunds = groupPaid.Sum(o => refundByOrderId.GetValueOrDefault(o.OrderId, 0m));
                return new SalesReportTerminalBreakdownDto
                {
                    BranchId = firstAttr.BranchId,
                    BranchName = firstAttr.BranchName,
                    OutletId = firstAttr.OutletId,
                    OutletName = firstAttr.OutletName,
                    TerminalId = firstAttr.TerminalId,
                    TerminalCode = firstAttr.TerminalCode,
                    TerminalName = firstAttr.TerminalName,
                    IsLegacyUnknownTerminal = firstAttr.IsLegacy,
                    TransactionCount = groupPaid.Count,
                    VoidedCount = groupVoided.Count,
                    GrossSales = Math.Round(groupPaid.Sum(o => o.Subtotal), 2, MidpointRounding.AwayFromZero),
                    TotalDiscounts = Math.Round(groupPaid.Sum(o => o.DiscountAmount), 2, MidpointRounding.AwayFromZero),
                    TotalRefunds = Math.Round(groupRefunds, 2, MidpointRounding.AwayFromZero),
                    NetSales = Math.Round(groupPaid.Sum(o => o.TotalAmount), 2, MidpointRounding.AwayFromZero),
                    OutputVat = Math.Round(groupPaid.Sum(o => o.VatAmount), 2, MidpointRounding.AwayFromZero)
                };
            })
            .OrderBy(t => t.BranchName)
            .ThenBy(t => t.OutletName)
            .ThenBy(t => t.IsLegacyUnknownTerminal ? 1 : 0)
            .ThenBy(t => t.TerminalName)
            .ToList();

        string grouping = (request.Grouping ?? "DAILY").ToUpperInvariant();

        response.Breakdown = grouping switch
        {
            "WEEKLY" => paidOrders
                .GroupBy(o => System.Globalization.ISOWeek.GetWeekOfYear(o.CreatedAt) + "-" + o.CreatedAt.Year)
                .Select(g => new SalesReportGroupItemDto
                {
                    Key = g.Key,
                    Label = "Week " + g.Key,
                    TransactionCount = g.Count(),
                    GrossSales = g.Sum(o => o.Subtotal),
                    TotalDiscounts = g.Sum(o => o.DiscountAmount),
                    TotalRefunds = g.Sum(o => refundByOrderId.GetValueOrDefault(o.OrderId, 0m)),
                    NetSales = g.Sum(o => o.TotalAmount),
                    OutputVat = g.Sum(o => o.VatAmount)
                }).OrderBy(x => x.Key).ToList(),

            "MONTHLY" => paidOrders
                .GroupBy(o => o.CreatedAt.ToString("yyyy-MM"))
                .Select(g => new SalesReportGroupItemDto
                {
                    Key = g.Key,
                    Label = DateTime.Parse(g.Key + "-01").ToString("MMMM yyyy"),
                    TransactionCount = g.Count(),
                    GrossSales = g.Sum(o => o.Subtotal),
                    TotalDiscounts = g.Sum(o => o.DiscountAmount),
                    TotalRefunds = g.Sum(o => refundByOrderId.GetValueOrDefault(o.OrderId, 0m)),
                    NetSales = g.Sum(o => o.TotalAmount),
                    OutputVat = g.Sum(o => o.VatAmount)
                }).OrderBy(x => x.Key).ToList(),

            "CASHIER" => paidOrders
                .GroupBy(o => o.CashierId)
                .Select(g => new SalesReportGroupItemDto
                {
                    Key = g.Key,
                    Label = "Cashier: " + g.Key,
                    TransactionCount = g.Count(),
                    GrossSales = g.Sum(o => o.Subtotal),
                    TotalDiscounts = g.Sum(o => o.DiscountAmount),
                    TotalRefunds = g.Sum(o => refundByOrderId.GetValueOrDefault(o.OrderId, 0m)),
                    NetSales = g.Sum(o => o.TotalAmount),
                    OutputVat = g.Sum(o => o.VatAmount)
                }).OrderByDescending(x => x.NetSales).ToList(),

            "BRANCH" => paidOrders
                .Select(o => new { Order = o, Attr = ResolveAttribution(o) })
                .GroupBy(x => x.Attr.BranchId)
                .Select(g =>
                {
                    var attr = g.First().Attr;
                    return new SalesReportGroupItemDto
                    {
                        Key = g.Key.ToString(),
                        Label = $"{attr.BranchName} (Branch #{g.Key})",
                        BranchId = attr.BranchId,
                        BranchName = attr.BranchName,
                        TransactionCount = g.Count(),
                        GrossSales = g.Sum(x => x.Order.Subtotal),
                        TotalDiscounts = g.Sum(x => x.Order.DiscountAmount),
                        TotalRefunds = g.Sum(x => refundByOrderId.GetValueOrDefault(x.Order.OrderId, 0m)),
                        NetSales = g.Sum(x => x.Order.TotalAmount),
                        OutputVat = g.Sum(x => x.Order.VatAmount)
                    };
                }).OrderByDescending(x => x.NetSales).ToList(),

            "OUTLET" => paidOrders
                .Select(o => new { Order = o, Attr = ResolveAttribution(o) })
                .GroupBy(x => (x.Attr.BranchId, x.Attr.OutletId))
                .Select(g =>
                {
                    var attr = g.First().Attr;
                    return new SalesReportGroupItemDto
                    {
                        Key = $"{attr.BranchId}:{attr.OutletId}",
                        Label = $"{attr.BranchName} → {attr.OutletName}",
                        BranchId = attr.BranchId,
                        BranchName = attr.BranchName,
                        OutletId = attr.OutletId,
                        OutletName = attr.OutletName,
                        TransactionCount = g.Count(),
                        GrossSales = g.Sum(x => x.Order.Subtotal),
                        TotalDiscounts = g.Sum(x => x.Order.DiscountAmount),
                        TotalRefunds = g.Sum(x => refundByOrderId.GetValueOrDefault(x.Order.OrderId, 0m)),
                        NetSales = g.Sum(x => x.Order.TotalAmount),
                        OutputVat = g.Sum(x => x.Order.VatAmount)
                    };
                }).OrderByDescending(x => x.NetSales).ToList(),

            "TERMINAL" => paidOrders
                .Select(o => new { Order = o, Attr = ResolveAttribution(o) })
                .GroupBy(x => (x.Attr.BranchId, x.Attr.OutletId, x.Attr.TerminalId))
                .Select(g =>
                {
                    var attr = g.First().Attr;
                    return new SalesReportGroupItemDto
                    {
                        Key = $"{attr.BranchId}:{attr.OutletId}:{attr.TerminalId}",
                        Label = $"{attr.BranchName} → {attr.OutletName} → {attr.TerminalName} ({attr.TerminalCode})",
                        BranchId = attr.BranchId,
                        BranchName = attr.BranchName,
                        OutletId = attr.OutletId,
                        OutletName = attr.OutletName,
                        TerminalId = attr.TerminalId,
                        TerminalCode = attr.TerminalCode,
                        TerminalName = attr.TerminalName,
                        TransactionCount = g.Count(),
                        GrossSales = g.Sum(x => x.Order.Subtotal),
                        TotalDiscounts = g.Sum(x => x.Order.DiscountAmount),
                        TotalRefunds = g.Sum(x => refundByOrderId.GetValueOrDefault(x.Order.OrderId, 0m)),
                        NetSales = g.Sum(x => x.Order.TotalAmount),
                        OutputVat = g.Sum(x => x.Order.VatAmount)
                    };
                }).OrderByDescending(x => x.NetSales).ToList(),

            "PAYMENT" => response.PaymentBreakdown
                .Select(p => new SalesReportGroupItemDto
                {
                    Key = p.PaymentMethodCode,
                    Label = $"Payment Method: {p.PaymentMethodName} ({p.PaymentMethodCode})",
                    TransactionCount = p.PaymentCount,
                    GrossSales = p.GrossCollected,
                    TotalDiscounts = 0m,
                    TotalRefunds = p.RefundedAmount,
                    NetSales = p.GrossCollected,
                    OutputVat = 0m
                }).ToList(),

            _ => paidOrders
                .GroupBy(o => o.CreatedAt.ToString("yyyy-MM-dd"))
                .Select(g => new SalesReportGroupItemDto
                {
                    Key = g.Key,
                    Label = g.Key,
                    TransactionCount = g.Count(),
                    GrossSales = g.Sum(o => o.Subtotal),
                    TotalDiscounts = g.Sum(o => o.DiscountAmount),
                    TotalRefunds = g.Sum(o => refundByOrderId.GetValueOrDefault(o.OrderId, 0m)),
                    NetSales = g.Sum(o => o.TotalAmount),
                    OutputVat = g.Sum(o => o.VatAmount)
                }).OrderBy(x => x.Key).ToList()
        };

        return response;
    }
}

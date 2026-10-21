using Microsoft.EntityFrameworkCore;
using System;
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
            .Include(o => o.Payments)
            .Where(o => o.PaymentStatus == "PAID" && o.OrderStatus != "CANCELLED");

        if (request.DateFrom.HasValue)
            query = query.Where(o => o.CreatedAt >= request.DateFrom.Value);

        if (request.DateTo.HasValue)
            query = query.Where(o => o.CreatedAt <= request.DateTo.Value);

        if (request.BranchId.HasValue)
            query = query.Where(o => o.BranchId == request.BranchId.Value);

        if (request.OutletId.HasValue)
            query = query.Where(o => o.OutletId == request.OutletId.Value);
        if (request.TerminalId.HasValue)
            query = query.Where(o => o.TerminalId == request.TerminalId.Value);

        if (!string.IsNullOrWhiteSpace(request.CashierId))
            query = query.Where(o => o.CashierId == request.CashierId);

        if (!string.IsNullOrWhiteSpace(request.PaymentMethod))
            query = query.Where(o => o.Payments.Any(p => p.PaymentMethod == request.PaymentMethod));

        var orders = await query.ToListAsync(cancellationToken);

        var response = new SalesReportResponseDto
        {
            GeneratedAt = DateTime.UtcNow,
            Parameters = request,
            CompletedTransactionsCount = orders.Count,
            GrossSales = orders.Sum(o => o.Subtotal),
            TotalDiscounts = orders.Sum(o => o.DiscountAmount),
            NetSales = orders.Sum(o => o.TotalAmount),
            VatableSales = orders.Sum(o => o.VatableSales),
            VatExemptSales = orders.Sum(o => o.VatExemptSales),
            ZeroRatedSales = orders.Sum(o => o.ZeroRatedSales),
            OutputVat = orders.Sum(o => o.VatAmount),
            TotalPaymentsCollected = orders.SelectMany(o => o.Payments).Where(p => p.PaymentStatus == "SUCCESS").Sum(p => p.Amount)
        };

        response.AverageTransactionValue = response.CompletedTransactionsCount > 0 
            ? Math.Round(response.NetSales / response.CompletedTransactionsCount, 2) 
            : 0m;

        string grouping = (request.Grouping ?? "DAILY").ToUpperInvariant();

        response.Breakdown = grouping switch
        {
            "WEEKLY" => orders
                .GroupBy(o => System.Globalization.ISOWeek.GetWeekOfYear(o.CreatedAt) + "-" + o.CreatedAt.Year)
                .Select(g => new SalesReportGroupItemDto
                {
                    Key = g.Key,
                    Label = "Week " + g.Key,
                    TransactionCount = g.Count(),
                    GrossSales = g.Sum(o => o.Subtotal),
                    TotalDiscounts = g.Sum(o => o.DiscountAmount),
                    NetSales = g.Sum(o => o.TotalAmount),
                    OutputVat = g.Sum(o => o.VatAmount)
                }).OrderBy(x => x.Key).ToList(),

            "MONTHLY" => orders
                .GroupBy(o => o.CreatedAt.ToString("yyyy-MM"))
                .Select(g => new SalesReportGroupItemDto
                {
                    Key = g.Key,
                    Label = DateTime.Parse(g.Key + "-01").ToString("MMMM yyyy"),
                    TransactionCount = g.Count(),
                    GrossSales = g.Sum(o => o.Subtotal),
                    TotalDiscounts = g.Sum(o => o.DiscountAmount),
                    NetSales = g.Sum(o => o.TotalAmount),
                    OutputVat = g.Sum(o => o.VatAmount)
                }).OrderBy(x => x.Key).ToList(),

            "CASHIER" => orders
                .GroupBy(o => o.CashierId)
                .Select(g => new SalesReportGroupItemDto
                {
                    Key = g.Key,
                    Label = "Cashier: " + g.Key,
                    TransactionCount = g.Count(),
                    GrossSales = g.Sum(o => o.Subtotal),
                    TotalDiscounts = g.Sum(o => o.DiscountAmount),
                    NetSales = g.Sum(o => o.TotalAmount),
                    OutputVat = g.Sum(o => o.VatAmount)
                }).OrderByDescending(x => x.NetSales).ToList(),

            "BRANCH" => orders
                .GroupBy(o => o.BranchId)
                .Select(g => new SalesReportGroupItemDto
                {
                    Key = g.Key.ToString(),
                    Label = "Branch ID: " + g.Key,
                    TransactionCount = g.Count(),
                    GrossSales = g.Sum(o => o.Subtotal),
                    TotalDiscounts = g.Sum(o => o.DiscountAmount),
                    NetSales = g.Sum(o => o.TotalAmount),
                    OutputVat = g.Sum(o => o.VatAmount)
                }).OrderByDescending(x => x.NetSales).ToList(),

            _ => orders
                .GroupBy(o => o.CreatedAt.ToString("yyyy-MM-dd"))
                .Select(g => new SalesReportGroupItemDto
                {
                    Key = g.Key,
                    Label = g.Key,
                    TransactionCount = g.Count(),
                    GrossSales = g.Sum(o => o.Subtotal),
                    TotalDiscounts = g.Sum(o => o.DiscountAmount),
                    NetSales = g.Sum(o => o.TotalAmount),
                    OutputVat = g.Sum(o => o.VatAmount)
                }).OrderBy(x => x.Key).ToList()
        };

        return response;
    }
}

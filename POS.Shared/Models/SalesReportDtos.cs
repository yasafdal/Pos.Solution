using System;
using System.Collections.Generic;

namespace POS.Shared.Models;

public class SalesReportRequestDto
{
    public DateTime? DateFrom { get; set; }
    public DateTime? DateTo { get; set; }
    public int? BranchId { get; set; }
    public int? TerminalId { get; set; }
    public int? OutletId { get; set; }
    public string? CashierId { get; set; }
    public string? PaymentMethod { get; set; }
    public string? Grouping { get; set; } = "DAILY"; // DAILY, WEEKLY, MONTHLY, CASHIER, BRANCH, PAYMENT
}

public class SalesReportResponseDto
{
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
    public SalesReportRequestDto Parameters { get; set; } = new();
    public decimal GrossSales { get; set; }
    public decimal TotalDiscounts { get; set; }
    public decimal NetSales { get; set; }
    public decimal VatableSales { get; set; }
    public decimal VatExemptSales { get; set; }
    public decimal ZeroRatedSales { get; set; }
    public decimal OutputVat { get; set; }
    public decimal TotalPaymentsCollected { get; set; }
    public int CompletedTransactionsCount { get; set; }
    public decimal AverageTransactionValue { get; set; }
    public List<SalesReportGroupItemDto> Breakdown { get; set; } = new();
}

public class SalesReportGroupItemDto
{
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public int TransactionCount { get; set; }
    public decimal GrossSales { get; set; }
    public decimal TotalDiscounts { get; set; }
    public decimal NetSales { get; set; }
    public decimal OutputVat { get; set; }
}

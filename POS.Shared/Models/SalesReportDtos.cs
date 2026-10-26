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
    public string? Grouping { get; set; } = "DAILY"; // DAILY, WEEKLY, MONTHLY, CASHIER, BRANCH, OUTLET, TERMINAL, PAYMENT
}

public class SalesReportResponseDto
{
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
    public SalesReportRequestDto Parameters { get; set; } = new();
    public decimal GrossSales { get; set; }
    public decimal TotalDiscounts { get; set; }
    public decimal TotalRefunds { get; set; }
    public decimal NetSales { get; set; }
    public decimal NetSalesAfterRefunds => Math.Round(NetSales - TotalRefunds, 2, MidpointRounding.AwayFromZero);
    public decimal VatableSales { get; set; }
    public decimal VatExemptSales { get; set; }
    public decimal ZeroRatedSales { get; set; }
    public decimal OutputVat { get; set; }
    public decimal TotalPaymentsCollected { get; set; }
    public decimal NetPaymentsAfterRefunds => Math.Round(TotalPaymentsCollected - TotalRefunds, 2, MidpointRounding.AwayFromZero);
    public decimal CashSalesCollected { get; set; }
    public decimal NonCashSalesCollected { get; set; }
    public int CompletedTransactionsCount { get; set; }
    public int VoidedTransactionsCount { get; set; }
    public decimal VoidedAmount { get; set; }
    public int ReturnTransactionsCount { get; set; }
    public decimal AverageTransactionValue { get; set; }
    public List<SalesReportGroupItemDto> Breakdown { get; set; } = new();
    public List<SalesReportPaymentBreakdownDto> PaymentBreakdown { get; set; } = new();
    public List<SalesReportTerminalBreakdownDto> TerminalBreakdown { get; set; } = new();
}

public class SalesReportGroupItemDto
{
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public int? BranchId { get; set; }
    public string? BranchName { get; set; }
    public int? OutletId { get; set; }
    public string? OutletName { get; set; }
    public int? TerminalId { get; set; }
    public string? TerminalCode { get; set; }
    public string? TerminalName { get; set; }
    public int TransactionCount { get; set; }
    public decimal GrossSales { get; set; }
    public decimal TotalDiscounts { get; set; }
    public decimal TotalRefunds { get; set; }
    public decimal NetSales { get; set; }
    public decimal NetSalesAfterRefunds => Math.Round(NetSales - TotalRefunds, 2, MidpointRounding.AwayFromZero);
    public decimal OutputVat { get; set; }
}

public class SalesReportPaymentBreakdownDto
{
    public string PaymentMethodCode { get; set; } = string.Empty;
    public string PaymentMethodName { get; set; } = string.Empty;
    public bool IsCash { get; set; }
    public int PaymentCount { get; set; }
    public decimal GrossCollected { get; set; }
    public decimal RefundedAmount { get; set; }
    public decimal NetCollected => Math.Round(GrossCollected - RefundedAmount, 2, MidpointRounding.AwayFromZero);
}

public class SalesReportTerminalBreakdownDto
{
    public int BranchId { get; set; }
    public string BranchName { get; set; } = string.Empty;
    public int OutletId { get; set; }
    public string OutletName { get; set; } = string.Empty;
    public int TerminalId { get; set; }
    public string TerminalCode { get; set; } = string.Empty;
    public string TerminalName { get; set; } = "Unknown / Legacy Terminal";
    public bool IsLegacyUnknownTerminal { get; set; }
    public int TransactionCount { get; set; }
    public int VoidedCount { get; set; }
    public decimal GrossSales { get; set; }
    public decimal TotalDiscounts { get; set; }
    public decimal TotalRefunds { get; set; }
    public decimal NetSales { get; set; }
    public decimal NetSalesAfterRefunds => Math.Round(NetSales - TotalRefunds, 2, MidpointRounding.AwayFromZero);
    public decimal OutputVat { get; set; }
}

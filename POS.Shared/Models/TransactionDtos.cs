using System;
using System.Collections.Generic;

namespace POS.Shared.Models;

public class TransactionFilterDto
{
    public DateTime? DateFrom { get; set; }
    public DateTime? DateTo { get; set; }
    public int? BranchId { get; set; }
    public int? OutletId { get; set; }
    public string? CashierId { get; set; }
    public string? OrderNumber { get; set; }
    public string? InvoiceNumber { get; set; }
    public string? PaymentMethod { get; set; }
    public string? OrderStatus { get; set; }
    public string? PaymentStatus { get; set; }
    public string? DiscountCode { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;
}

public class TransactionSummaryDto
{
    public int TotalTransactions { get; set; }
    public decimal GrossSales { get; set; }
    public decimal TotalDiscounts { get; set; }
    public decimal NetSales { get; set; }
    public decimal VatableSales { get; set; }
    public decimal VatExemptSales { get; set; }
    public decimal ZeroRatedSales { get; set; }
    public decimal TotalVat { get; set; }
}

using System;
using System.Collections.Generic;

namespace POS.Shared.Models;

public class OrderResponseDto
{
    public long OrderId { get; set; }
    public string OrderNumber { get; set; } = null!;
    public int BranchId { get; set; }
    public string BranchName { get; set; } = string.Empty;
    public int OutletId { get; set; }
    public string OutletName { get; set; } = string.Empty;
    public int TerminalId { get; set; }
    public string TerminalCode { get; set; } = string.Empty;
    public string TerminalName { get; set; } = "Unknown / Legacy Terminal";
    public string? TableNumber { get; set; }
    public int? HeadCount { get; set; }
    public string CashierId { get; set; } = null!;
    public long? ShiftId { get; set; }
    public string OrderType { get; set; } = null!;
    public string OrderStatus { get; set; } = null!;
    public string PaymentStatus { get; set; } = null!;
    public decimal Subtotal { get; set; }
    public decimal DiscountAmount { get; set; }
    public decimal TotalAmount { get; set; }
    public string? CustomerName { get; set; }
    public string? CustomerAddress { get; set; }
    public string? CustomerTin { get; set; }
    public string? CustomerBusinessStyle { get; set; }
    public decimal VatableSales { get; set; }
    public decimal VatExemptSales { get; set; }
    public decimal ZeroRatedSales { get; set; }
    public decimal VatAmount { get; set; }
    public string? DiscountCode { get; set; }
    public string? CustomerCardNumber { get; set; }
    public string? InvoiceNumber { get; set; }
    public DateTime? InvoiceIssuedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public List<OrderItemResponseDto> Items { get; set; } = new();
    public List<PaymentResponseDto> Payments { get; set; } = new();
}

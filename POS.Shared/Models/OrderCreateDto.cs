using System.Collections.Generic;

namespace POS.Shared.Models;

public class OrderCreateDto
{
    public int BranchId { get; set; }
    public int OutletId { get; set; }
    public int TerminalId { get; set; }
    public string? TableNumber { get; set; }
    public int? HeadCount { get; set; }
    public string CashierId { get; set; } = null!;
    public long? ShiftId { get; set; }
    public string OrderType { get; set; } = "DINING";
    public decimal DiscountAmount { get; set; }
    public int? DiscountTypeId { get; set; }
    public string? DiscountCode { get; set; }
    public string? CustomerCardNumber { get; set; }
    public string? CustomerName { get; set; }
    public string? CustomerAddress { get; set; }
    public string? CustomerTin { get; set; }
    public string? CustomerBusinessStyle { get; set; }
    public string? OfflineInvoiceNumber { get; set; } // Preserves offline-issued invoice number during sync
    public List<OrderItemCreateDto> Items { get; set; } = new();
}

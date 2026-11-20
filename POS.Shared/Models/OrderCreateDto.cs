using System;
using System.Collections.Generic;

namespace POS.Shared.Models;

public class OrderCreateDto
{
    public string? ClientShiftId { get; set; }
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
    public string? DiscountMethod { get; set; }
    public decimal? DiscountValue { get; set; }
    public decimal ManualDiscountPercentage { get; set; }
    public decimal ManualDiscountAmount { get; set; }
    public decimal PromoDiscountAmount { get; set; }
    public string? CustomerCardNumber { get; set; }
    public string? CustomerName { get; set; }
    public string? CustomerAddress { get; set; }
    public string? CustomerTin { get; set; }
    public string? CustomerBusinessStyle { get; set; }
    public string? OfflineOrderNumber { get; set; } // Preserves offline-issued sequential order number during sync
    public string? OfflineInvoiceNumber { get; set; } // Preserves offline-issued invoice number during sync
    public DateTimeOffset? TransactionTimestamp { get; set; }
    public string? DeviceTimeZoneId { get; set; }
    public int? DeviceUtcOffsetMinutes { get; set; }
    public List<OrderItemCreateDto> Items { get; set; } = new();
    public List<PaymentSplitDto>? Splits { get; set; }
}

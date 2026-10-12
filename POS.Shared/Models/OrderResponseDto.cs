using System;
using System.Collections.Generic;

namespace POS.Shared.Models;

public class OrderResponseDto
{
    public long OrderId { get; set; }
    public string OrderNumber { get; set; } = null!;
    public int BranchId { get; set; }
    public int OutletId { get; set; }
    public string CashierId { get; set; } = null!;
    public long? ShiftId { get; set; }
    public string OrderType { get; set; } = null!;
    public string OrderStatus { get; set; } = null!;
    public string PaymentStatus { get; set; } = null!;
    public decimal Subtotal { get; set; }
    public decimal DiscountAmount { get; set; }
    public decimal TotalAmount { get; set; }
    public DateTime CreatedAt { get; set; }
    public List<OrderItemResponseDto> Items { get; set; } = new();
}

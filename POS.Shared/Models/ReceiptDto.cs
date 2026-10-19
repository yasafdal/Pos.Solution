namespace POS.Shared.Models;

public class ReceiptDto
{
    public long OrderId { get; set; }
    public string OrderNumber { get; set; } = string.Empty;
    public int BranchId { get; set; }
    public string BranchName { get; set; } = string.Empty;
    public string? BranchCode { get; set; }
    public string? BranchAddress { get; set; }
    public string? BranchPhone { get; set; }
    public int OutletId { get; set; }
    public string TerminalName { get; set; } = string.Empty;
    public string CashierId { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime PaidAt { get; set; }
    public decimal Subtotal { get; set; }
    public decimal VatableSales { get; set; }
    public decimal VatExemptSales { get; set; }
    public decimal ZeroRatedSales { get; set; }
    public decimal VatAmount { get; set; }
    public string? DiscountCode { get; set; }
    public string? CustomerCardNumber { get; set; }
    public decimal DiscountAmount { get; set; }
    public decimal TotalAmount { get; set; }
    public string? InvoiceNumber { get; set; }
    public DateTime? InvoiceIssuedAt { get; set; }
    public string? CustomerName { get; set; }
    public string? CustomerAddress { get; set; }
    public string? CustomerTin { get; set; }
    public string? CustomerBusinessStyle { get; set; }
    public bool IsReprint { get; set; }
    public bool IsVoid { get; set; }
    public string? VoidReason { get; set; }
    public bool IsReturn { get; set; }
    public string? ReturnReference { get; set; }
    public string? ReturnReason { get; set; }
    public List<OrderItemResponseDto> Items { get; set; } = new();
    public List<PaymentResponseDto> Payments { get; set; } = new();
}

using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace POS.Api.Models;

public partial class ReceiptReprintLog
{
    [Key]
    public long ReprintLogId { get; set; }

    public long OrderId { get; set; }

    [MaxLength(50)]
    public string? InvoiceNumber { get; set; }

    public int BranchId { get; set; }

    public int TerminalId { get; set; }

    [Required]
    [MaxLength(450)]
    public string CashierId { get; set; } = null!;

    [Required]
    [MaxLength(255)]
    public string ReprintReason { get; set; } = "CUSTOMER_REQUEST";

    public DateTime ReprintedAt { get; set; } = DateTime.UtcNow;

    [MaxLength(100)]
    public string? PrinterName { get; set; }

    [Required]
    [MaxLength(30)]
    public string PrintStatus { get; set; } = "SUCCESS";

    public string? ErrorMessage { get; set; }

    [ForeignKey("OrderId")]
    public virtual Order? Order { get; set; }
}

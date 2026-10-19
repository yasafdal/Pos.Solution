using System;
using System.ComponentModel.DataAnnotations;

namespace POS.Api.Models;

public partial class HeldSaleAuditLog
{
    [Key]
    public long AuditId { get; set; }

    public long? HeldSaleId { get; set; }

    [Required]
    [MaxLength(50)]
    public string HoldReference { get; set; } = null!;

    [Required]
    [MaxLength(450)]
    public string CashierId { get; set; } = null!;

    public int BranchId { get; set; }

    public int OutletId { get; set; }

    [Required]
    [MaxLength(30)]
    public string Action { get; set; } = null!; // HELD, RESUMED, CANCELLED, CONCURRENCY_CONFLICT

    [MaxLength(255)]
    public string? Details { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

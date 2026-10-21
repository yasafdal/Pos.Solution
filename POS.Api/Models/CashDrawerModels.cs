using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace POS.Api.Models;

public partial class CashMovementCategory
{
    [Key]
    public int CategoryId { get; set; }
    [Required]
    [MaxLength(100)]
    public string Name { get; set; } = null!;
    [MaxLength(255)]
    public string? Description { get; set; }
    [Required]
    [MaxLength(30)]
    public string MovementType { get; set; } = "PETTY_CASH"; // PETTY_CASH, CASH_OUT, CASH_IN, CASH_DROP
    public bool RequiresApproval { get; set; } = true;
    [Column(TypeName = "numeric(18,2)")]
    public decimal ApprovalThreshold { get; set; } = 0m;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public partial class CashDrawerShift
{
    [Key]
    public long ShiftId { get; set; }
    public int BranchId { get; set; }
    public int TerminalId { get; set; }
    [Required]
    [MaxLength(450)]
    public string CashierId { get; set; } = null!;
    [Column(TypeName = "numeric(18,2)")]
    public decimal OpeningFloat { get; set; }
    [Column(TypeName = "numeric(18,2)")]
    public decimal TotalCashSales { get; set; }
    [Column(TypeName = "numeric(18,2)")]
    public decimal TotalCashRefunds { get; set; }
    [Column(TypeName = "numeric(18,2)")]
    public decimal TotalCashIn { get; set; }
    [Column(TypeName = "numeric(18,2)")]
    public decimal TotalCashOut { get; set; }
    [Column(TypeName = "numeric(18,2)")]
    public decimal TotalCashDrops { get; set; }
    [Column(TypeName = "numeric(18,2)")]
    public decimal ExpectedBalance { get; set; }
    [Column(TypeName = "numeric(18,2)")]
    public decimal? ActualCountedBalance { get; set; }
    [Column(TypeName = "numeric(18,2)")]
    public decimal? Discrepancy { get; set; }
    [Required]
    [MaxLength(30)]
    public string Status { get; set; } = "OPEN";
    public DateTime OpenedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ClosedAt { get; set; }
    [MaxLength(255)]
    public string? Notes { get; set; }

    public virtual ICollection<CashDrawerMovement> Movements { get; set; } = new List<CashDrawerMovement>();
}

public partial class CashDrawerMovement
{
    [Key]
    public long MovementId { get; set; }
    public long ShiftId { get; set; }
    public int BranchId { get; set; }
    public int TerminalId { get; set; }
    [Required]
    [MaxLength(30)]
    public string MovementType { get; set; } = null!; // CASH_IN, CASH_OUT, PETTY_CASH, CASH_DROP, CASH_SALE, CASH_REFUND, REVERSAL
    public int? CategoryId { get; set; }
    [MaxLength(100)]
    public string? CategoryName { get; set; }
    [Column(TypeName = "numeric(18,2)")]
    public decimal Amount { get; set; }
    [Required]
    [MaxLength(255)]
    public string Reason { get; set; } = null!;
    [MaxLength(50)]
    public string? ReferenceNumber { get; set; }
    [MaxLength(100)]
    public string? ClientMovementId { get; set; }
    [Required]
    [MaxLength(30)]
    public string Status { get; set; } = "POSTED"; // PENDING_APPROVAL, POSTED, REJECTED, REVERSED
    [MaxLength(450)]
    public string? CreatedBy { get; set; }
    [Required]
    [MaxLength(450)]
    public string AuthorizedBy { get; set; } = null!;
    [MaxLength(450)]
    public string? ApprovedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ApprovedAt { get; set; }
    public DateTime? PostedAt { get; set; }
    [MaxLength(255)]
    public string? RejectionReason { get; set; }
    [MaxLength(450)]
    public string? ReversedBy { get; set; }
    public DateTime? ReversedAt { get; set; }
    [MaxLength(255)]
    public string? ReversalReason { get; set; }
    public long? OriginalMovementId { get; set; }

    public virtual CashDrawerShift CashDrawerShift { get; set; } = null!;
    public virtual CashMovementCategory? Category { get; set; }
}


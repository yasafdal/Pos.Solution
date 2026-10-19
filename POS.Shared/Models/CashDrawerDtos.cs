using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace POS.Shared.Models;

public class OpenShiftDto
{
    public int BranchId { get; set; }
    public int OutletId { get; set; }
    public decimal OpeningFloat { get; set; }
    public string? Notes { get; set; }
}

public class CashMovementCategoryDto
{
    public int CategoryId { get; set; }
    [Required, MaxLength(100)]
    public string Name { get; set; } = string.Empty;
    [MaxLength(255)]
    public string? Description { get; set; }
    [Required, MaxLength(30)]
    public string MovementType { get; set; } = "PETTY_CASH"; // PETTY_CASH, CASH_OUT, CASH_IN, CASH_DROP
    public bool RequiresApproval { get; set; } = true;
    public decimal ApprovalThreshold { get; set; } = 0m;
    public bool IsActive { get; set; } = true;
}

public class CashDrawerMovementDto
{
    public long ShiftId { get; set; }
    public int BranchId { get; set; }
    public int OutletId { get; set; }
    public string MovementType { get; set; } = "PETTY_CASH"; // CASH_IN, CASH_OUT, PETTY_CASH, CASH_DROP
    public int? CategoryId { get; set; }
    public string? CategoryName { get; set; }
    public decimal Amount { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string? ReferenceNumber { get; set; }
    public string? ClientMovementId { get; set; }
    public string? CreatedBy { get; set; }
    public string? SupervisorPin { get; set; }
    public DateTime? CreatedAt { get; set; }
}

public class CashMovementApproveRejectDto
{
    public string? SupervisorPin { get; set; }
    public string? Reason { get; set; }
}

public class CashMovementReverseDto
{
    [Required]
    public string Reason { get; set; } = string.Empty;
    public string? SupervisorPin { get; set; }
}

public class CashMovementResponseDto
{
    public long MovementId { get; set; }
    public long ShiftId { get; set; }
    public int BranchId { get; set; }
    public int OutletId { get; set; }
    public string MovementType { get; set; } = string.Empty;
    public int? CategoryId { get; set; }
    public string? CategoryName { get; set; }
    public decimal Amount { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string ReferenceNumber { get; set; } = string.Empty;
    public string? ClientMovementId { get; set; }
    public string Status { get; set; } = "POSTED"; // PENDING_APPROVAL, POSTED, REJECTED, REVERSED
    public string CreatedBy { get; set; } = string.Empty;
    public string AuthorizedBy { get; set; } = string.Empty;
    public string? ApprovedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public DateTime? PostedAt { get; set; }
    public string? RejectionReason { get; set; }
    public string? ReversedBy { get; set; }
    public DateTime? ReversedAt { get; set; }
    public string? ReversalReason { get; set; }
    public long? OriginalMovementId { get; set; }
    public string SyncStatus { get; set; } = "SYNCED"; // SYNCED, PENDING, NEEDS_ATTENTION, CONFLICT
}

public class CloseShiftDto
{
    public long ShiftId { get; set; }
    public decimal ActualCountedBalance { get; set; }
    public string? Notes { get; set; }
}

public class CashDrawerShiftDto
{
    public long ShiftId { get; set; }
    public int BranchId { get; set; }
    public int OutletId { get; set; }
    public string CashierId { get; set; } = null!;
    public decimal OpeningFloat { get; set; }
    public decimal TotalCashSales { get; set; }
    public decimal TotalCashRefunds { get; set; }
    public decimal TotalCashIn { get; set; }
    public decimal TotalCashOut { get; set; }
    public decimal TotalCashDrops { get; set; }
    public decimal TotalPettyCashOut { get; set; }
    public decimal ExpectedBalance { get; set; }
    public decimal? ActualCountedBalance { get; set; }
    public decimal? Discrepancy { get; set; }
    public string Status { get; set; } = null!;
    public DateTime OpenedAt { get; set; }
    public DateTime? ClosedAt { get; set; }
    public string? Notes { get; set; }
    public int PendingApprovalsCount { get; set; }
    public List<CashMovementResponseDto> Movements { get; set; } = new();
}

public class ShiftCashMovementSummaryDto
{
    public long ShiftId { get; set; }
    public int BranchId { get; set; }
    public int OutletId { get; set; }
    public string CashierId { get; set; } = string.Empty;
    public decimal OpeningCash { get; set; }
    public decimal CashSales { get; set; }
    public decimal CashPaidIn { get; set; }
    public decimal CashPaidOut { get; set; }
    public decimal PettyCashOut { get; set; }
    public decimal CashDrops { get; set; }
    public decimal CashRefunds { get; set; }
    public decimal ExpectedCash { get; set; }
    public decimal? ActualCountedCash { get; set; }
    public decimal? OverShortAmount { get; set; }
    public int PostedMovementCount { get; set; }
    public int PendingApprovalCount { get; set; }
    public int RejectedMovementCount { get; set; }
    public int ReversedMovementCount { get; set; }
}

public class CashMovementCategoryBreakdownDto
{
    public int? CategoryId { get; set; }
    public string CategoryName { get; set; } = string.Empty;
    public string MovementType { get; set; } = string.Empty;
    public int Count { get; set; }
    public decimal TotalAmount { get; set; }
}

public class CashMovementSummaryReportDto
{
    public decimal TotalPaidIn { get; set; }
    public decimal TotalPaidOut { get; set; }
    public decimal TotalPettyCash { get; set; }
    public decimal TotalCashDrops { get; set; }
    public decimal TotalReversed { get; set; }
    public decimal NetCashMovement => TotalPaidIn - TotalPaidOut - TotalCashDrops;
    public int TotalPostedCount { get; set; }
    public int TotalPendingCount { get; set; }
    public int TotalRejectedCount { get; set; }
    public int TotalReversedCount { get; set; }
    public List<CashMovementCategoryBreakdownDto> ByCategory { get; set; } = new();
}


using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace POS.Shared.Models;

public class OpenShiftDto
{
    public int BranchId { get; set; }
    public int TerminalId { get; set; }
    [Required, Range(typeof(decimal), "0", "9999999999999999.99")]
    public decimal? OpeningFloat { get; set; }
    public string? ClientShiftId { get; set; }
    public string? CashierId { get; set; }
    public DateTime? OpenedAt { get; set; }
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
    public int TerminalId { get; set; }
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
    public string BranchName { get; set; } = string.Empty;
    public int OutletId { get; set; }
    public string OutletName { get; set; } = string.Empty;
    public int TerminalId { get; set; }
    public string TerminalCode { get; set; } = string.Empty;
    public string TerminalName { get; set; } = "Unknown / Legacy Terminal";
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

public class CashDenominationLineDto
{
    public string Type { get; set; } = "BILL"; // COIN or BILL
    public decimal Denomination { get; set; }
    public int Quantity { get; set; }
    public decimal Subtotal { get; set; }
    public string Label => Denomination < 1m ? $"₱{Denomination:0.00}" : $"₱{Denomination:N0}";
}

public static class CashDenominationCatalog
{
    public static readonly decimal[] CoinDenominations = [0.01m, 0.05m, 0.10m, 0.25m, 1.00m, 5.00m, 10.00m, 20.00m];
    public static readonly decimal[] BillDenominations = [20.00m, 50.00m, 100.00m, 200.00m, 500.00m, 1000.00m];

    public static List<CashDenominationLineDto> CreateDefaultLines()
    {
        var list = new List<CashDenominationLineDto>(CoinDenominations.Length + BillDenominations.Length);
        foreach (var d in CoinDenominations)
            list.Add(new CashDenominationLineDto { Type = "COIN", Denomination = d, Quantity = 0, Subtotal = 0m });
        foreach (var d in BillDenominations)
            list.Add(new CashDenominationLineDto { Type = "BILL", Denomination = d, Quantity = 0, Subtotal = 0m });
        return list;
    }

    public static bool IsValidDenomination(string? type, decimal denomination)
    {
        if (string.Equals(type, "COIN", StringComparison.OrdinalIgnoreCase))
            return Array.IndexOf(CoinDenominations, denomination) >= 0;
        if (string.Equals(type, "BILL", StringComparison.OrdinalIgnoreCase))
            return Array.IndexOf(BillDenominations, denomination) >= 0;
        return false;
    }

    public static bool TryValidateAndCalculate(
        IReadOnlyList<CashDenominationLineDto>? input,
        out List<CashDenominationLineDto> normalized,
        out decimal totalCountedCash,
        out string? errorMessage)
    {
        normalized = CreateDefaultLines();
        totalCountedCash = 0m;
        errorMessage = null;

        if (input == null || input.Count == 0)
            return true;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in input)
        {
            if (row == null) continue;
            string normType = (row.Type ?? string.Empty).Trim().ToUpperInvariant();
            if (!IsValidDenomination(normType, row.Denomination))
            {
                errorMessage = $"Invalid cash denomination: {row.Type} ₱{row.Denomination:0.00}.";
                return false;
            }
            if (row.Quantity < 0)
            {
                errorMessage = $"Denomination quantity for {normType} ₱{row.Denomination:0.00} cannot be negative.";
                return false;
            }
            string key = $"{normType}:{row.Denomination:0.00}";
            if (!seen.Add(key))
            {
                errorMessage = $"Duplicate denomination entry for {normType} ₱{row.Denomination:0.00}.";
                return false;
            }

            var target = normalized.Find(n => n.Type == normType && n.Denomination == row.Denomination);
            if (target != null)
            {
                target.Quantity = row.Quantity;
                target.Subtotal = Math.Round(target.Denomination * target.Quantity, 2, MidpointRounding.AwayFromZero);
            }
        }

        foreach (var line in normalized)
        {
            line.Subtotal = Math.Round(line.Denomination * line.Quantity, 2, MidpointRounding.AwayFromZero);
            totalCountedCash += line.Subtotal;
        }
        totalCountedCash = Math.Round(totalCountedCash, 2, MidpointRounding.AwayFromZero);
        return true;
    }
}

public class PaymentMethodDeclarationDto
{
    public string PaymentMethodCode { get; set; } = string.Empty;
    public string? PaymentMethodName { get; set; }
    public bool IsActive { get; set; } = true;
    public int DisplayOrder { get; set; }
    public decimal DeclaredAmount { get; set; }
}

public class CashierDeclarationReportDto
{
    public long ShiftId { get; set; }
    public string? ClientShiftId { get; set; }
    public int BranchId { get; set; }
    public string BranchName { get; set; } = string.Empty;
    public string? BranchCode { get; set; }
    public string? BranchAddress { get; set; }
    public string? BranchPhone { get; set; }
    public int OutletId { get; set; }
    public string OutletName { get; set; } = string.Empty;
    public int TerminalId { get; set; }
    public string TerminalCode { get; set; } = string.Empty;
    public string TerminalName { get; set; } = string.Empty;
    public string? PTUNumber { get; set; }
    public string? MINNumber { get; set; }
    public string? SerialNumber { get; set; }
    public string CashierId { get; set; } = string.Empty;
    public DateTime OpenedAt { get; set; }
    public DateTime? ClosedAt { get; set; }
    public DateTime SubmittedAt { get; set; } = DateTime.UtcNow;
    public bool IsReprint { get; set; }
    public decimal TotalCountedCash { get; set; }
    public decimal TotalNonCashDeclared { get; set; }
    public bool HasRecordedDenominations { get; set; } = true;
    public List<CashDenominationLineDto> Denominations { get; set; } = new();
    public List<PaymentMethodDeclarationDto> PaymentDeclarations { get; set; } = new();
    public string? Notes { get; set; }
}

public class ShiftRecountRequestDto
{
    public long ShiftId { get; set; }
    public string? SupervisorPin { get; set; }
    [Required]
    public string Reason { get; set; } = string.Empty;
    public decimal ActualCountedBalance { get; set; }
    public List<CashDenominationLineDto>? Denominations { get; set; }
    public List<PaymentMethodDeclarationDto>? PaymentDeclarations { get; set; }
    public string? Notes { get; set; }
}

public class ShiftRecountAuditDto
{
    public int RecountNumber { get; set; }
    public long ShiftId { get; set; }
    public string CorrectedBy { get; set; } = string.Empty;
    public DateTime CorrectedAt { get; set; } = DateTime.UtcNow;
    public string Reason { get; set; } = string.Empty;
    public decimal OriginalCountedCash { get; set; }
    public decimal RevisedCountedCash { get; set; }
    public decimal OriginalCashVariance { get; set; }
    public decimal RevisedCashVariance { get; set; }
    public List<CashDenominationLineDto> OriginalDenominations { get; set; } = new();
    public List<CashDenominationLineDto> RevisedDenominations { get; set; } = new();
    public List<PaymentMethodDeclarationDto> OriginalPaymentDeclarations { get; set; } = new();
    public List<PaymentMethodDeclarationDto> RevisedPaymentDeclarations { get; set; } = new();
}

public class PaymentMethodReconciliationDto
{
    public string PaymentMethodCode { get; set; } = string.Empty;
    public string PaymentMethodName { get; set; } = string.Empty;
    public bool IsCash { get; set; }
    public bool IsActive { get; set; } = true;
    public int DisplayOrder { get; set; }
    public decimal GrossSalesAmount { get; set; }
    public decimal RefundAmount { get; set; }
    public decimal NetSalesAmount { get; set; }
    public decimal ExpectedAmount { get; set; }
    public decimal DeclaredAmount { get; set; }
    public decimal Variance { get; set; }
    public string Status { get; set; } = "BALANCED"; // BALANCED, SHORT, OVER
    public string DeclarationSource => IsCash ? "PHYSICAL_COUNT" : "CASHIER_DECLARED";
    public string DeclarationSourceLabel => IsCash ? "Physical Drawer Count" : "Cashier-Declared (Not Provider-Verified)";

    public static string ComputeStatus(decimal variance) =>
        variance == 0m ? "BALANCED" : (variance < 0m ? "SHORT" : "OVER");
}

public class XReadingReportDto
{
    public long ShiftId { get; set; }
    public string? ClientShiftId { get; set; }
    public int BranchId { get; set; }
    public string BranchName { get; set; } = string.Empty;
    public string? BranchCode { get; set; }
    public string? BranchAddress { get; set; }
    public string? BranchPhone { get; set; }
    public int OutletId { get; set; }
    public string OutletName { get; set; } = string.Empty;
    public int TerminalId { get; set; }
    public string TerminalCode { get; set; } = string.Empty;
    public string TerminalName { get; set; } = string.Empty;
    public string? PTUNumber { get; set; }
    public string? MINNumber { get; set; }
    public string? SerialNumber { get; set; }
    public string CashierId { get; set; } = string.Empty;
    public DateTime OpenedAt { get; set; }
    public DateTime? ClosedAt { get; set; }
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
    public bool IsReprint { get; set; }

    // Non-resetting X-Reading counters (Business Day 4:00 AM scope & Shift scope)
    public DateTime ReportingPeriodStart { get; set; }
    public DateTime ReportingPeriodEnd { get; set; }
    public bool ReportingPeriodMatchesShift { get; set; } = true;
    public string? BeginningInvoiceNumber { get; set; }
    public string? EndingInvoiceNumber { get; set; }
    public int CompletedTransactionsCount { get; set; }
    public int VoidedTransactionsCount { get; set; }
    public int ReturnTransactionsCount { get; set; }
    public decimal GrossSales { get; set; }
    public decimal TotalDiscounts { get; set; }
    public decimal TotalRefundsAndVoids { get; set; }
    public decimal NetSales { get; set; }
    public decimal VatableSales { get; set; }
    public decimal VatExemptSales { get; set; }
    public decimal ZeroRatedSales { get; set; }
    public decimal VatAmount { get; set; }

    // Shift-specific sales totals (never includes Opening Float or Cash Movements)
    public int ShiftTransactionsCount { get; set; }
    public decimal ShiftGrossSales { get; set; }
    public decimal ShiftDiscounts { get; set; }
    public decimal ShiftRefundsAndVoids { get; set; }
    public decimal ShiftNetSales { get; set; }
    public decimal ShiftCashNetSales { get; set; }
    public decimal ShiftNonCashNetSales { get; set; }

    // Cash Drawer Ledger (Physical Drawer Cash)
    public decimal OpeningFloat { get; set; }
    public decimal CashSalesCollected { get; set; }
    public decimal CashRefunds { get; set; }
    public decimal NetCashSales { get; set; }
    public decimal TotalCashIn { get; set; }
    public decimal TotalCashOut { get; set; }
    public decimal TotalCashDrops { get; set; }
    public decimal ExpectedCashInDrawer { get; set; }
    public decimal CountedCashInDrawer { get; set; }
    public decimal CashVariance { get; set; }
    public string CashReconciliationStatus { get; set; } = "BALANCED";

    // Breakdowns
    public bool HasRecordedDenominations { get; set; } = true;
    public List<CashDenominationLineDto> Denominations { get; set; } = new();
    public List<PaymentMethodReconciliationDto> PaymentReconciliations { get; set; } = new();
    public CashierDeclarationReportDto? OriginalDeclaration { get; set; }
    public List<ShiftRecountAuditDto> RecountHistory { get; set; } = new();
    public bool IsOverallBalanced { get; set; } = true;
    public string OverallStatus { get; set; } = "BALANCED";
    public string? Notes { get; set; }
}

public class CloseShiftDto
{
    public long ShiftId { get; set; }
    public string? ClientShiftId { get; set; }
    public string? ClientCloseId { get; set; }
    public int? BranchId { get; set; }
    public int? TerminalId { get; set; }
    public string? CashierId { get; set; }
    public DateTime? ClosedAt { get; set; }
    public decimal ActualCountedBalance { get; set; }
    public List<CashDenominationLineDto>? Denominations { get; set; }
    public List<PaymentMethodDeclarationDto>? PaymentDeclarations { get; set; }
    public bool IsDeclarationConfirmed { get; set; }
    public string? Notes { get; set; }
}

public class CashDrawerShiftDto
{
    // IDs included in the same server snapshot as TotalCashSales. Null denotes a legacy response.
    public List<string>? AcknowledgedLocalTransactionIds { get; set; }
    public bool OpeningConfirmed { get; set; }
    public bool IsBlindCloseRedacted { get; set; }
    public string? ClientShiftId { get; set; }
    public string? ClientCloseId { get; set; }
    public long ShiftId { get; set; }
    public int BranchId { get; set; }
    public string BranchName { get; set; } = string.Empty;
    public int OutletId { get; set; }
    public string OutletName { get; set; } = string.Empty;
    public int TerminalId { get; set; }
    public string TerminalCode { get; set; } = string.Empty;
    public string TerminalName { get; set; } = "Unknown / Legacy Terminal";
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
    public bool HasRecordedDenominations { get; set; } = true;
    public List<CashDenominationLineDto> Denominations { get; set; } = new();
    public List<PaymentMethodDeclarationDto> ConfiguredNonCashMethods { get; set; } = new();
    public List<PaymentMethodReconciliationDto> PaymentReconciliations { get; set; } = new();
    public decimal TotalNonCashExpected { get; set; }
    public decimal TotalNonCashDeclared { get; set; }
    // Redacted (blind-close) or not-yet-counted shifts expose no reconciliation result.
    private bool IsReconciliationUnavailable => IsBlindCloseRedacted || (PaymentReconciliations.Count == 0 && Discrepancy == null);
    public decimal TotalNonCashVariance => IsReconciliationUnavailable ? 0m : Math.Round(TotalNonCashDeclared - TotalNonCashExpected, 2, MidpointRounding.AwayFromZero);
    public bool HasAnyDiscrepancy => !IsReconciliationUnavailable && (PaymentReconciliations.Count > 0
        ? PaymentReconciliations.Any(r => r.Variance != 0m)
        : (Discrepancy ?? 0m) != 0m);
    public string OverallReconciliationStatus => IsReconciliationUnavailable ? "NOT RECONCILED" : PaymentReconciliations.Count > 0
        ? (PaymentReconciliations.All(r => r.Variance == 0m)
            ? "BALANCED"
            : (PaymentReconciliations.Any(r => r.Variance < 0m) && PaymentReconciliations.Any(r => r.Variance > 0m)
                ? "MIXED DISCREPANCY"
                : (PaymentReconciliations.Any(r => r.Variance < 0m) ? "SHORT" : "OVER")))
        : PaymentMethodReconciliationDto.ComputeStatus(Discrepancy ?? 0m);
    public decimal TotalNetSales { get; set; }
    public CashierDeclarationReportDto? CashierDeclaration { get; set; }
    public CashierDeclarationReportDto? OriginalDeclaration { get; set; }
    public List<ShiftRecountAuditDto> RecountHistory { get; set; } = new();
    public XReadingReportDto? XReading { get; set; }
}

public class ShiftCashMovementSummaryDto
{
    public long ShiftId { get; set; }
    public int BranchId { get; set; }
    public int TerminalId { get; set; }
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

public class CashMovementTerminalBreakdownDto
{
    public int BranchId { get; set; }
    public string BranchName { get; set; } = string.Empty;
    public int OutletId { get; set; }
    public string OutletName { get; set; } = string.Empty;
    public int TerminalId { get; set; }
    public string TerminalCode { get; set; } = string.Empty;
    public string TerminalName { get; set; } = "Unknown / Legacy Terminal";
    public int PostedCount { get; set; }
    public decimal TotalPaidIn { get; set; }
    public decimal TotalPaidOut { get; set; }
    public decimal TotalPettyCash { get; set; }
    public decimal TotalCashDrops { get; set; }
    public decimal NetCashMovement => TotalPaidIn - TotalPaidOut - TotalCashDrops;
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
    public List<CashMovementTerminalBreakdownDto> ByTerminal { get; set; } = new();
}

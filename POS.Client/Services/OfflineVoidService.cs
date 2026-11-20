using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using POS.Client.Data;
using POS.Shared.Constants;
using POS.Shared.Helpers;
using POS.Shared.Models;
using POS.Shared.Services;

namespace POS.Client.Services;

public class OfflineVoidRequest
{
    public string? LocalOrderId { get; set; }
    public long? ServerOrderId { get; set; }
    public string? InvoiceNumber { get; set; }
    public string? OrderNumber { get; set; }
    public string VoidReason { get; set; } = string.Empty;
    public string SupervisorPin { get; set; } = string.Empty;
    public OrderResponseDto? OrderSnapshot { get; set; }
}

public class OfflineVoidResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public VoidResponseDto? Response { get; set; }

    public static OfflineVoidResult Fail(string message) => new() { Success = false, ErrorMessage = message };
}

public interface IOfflineVoidService
{
    Task<bool> HasAuthorizedLocalSupervisorAsync();
    Task<OfflineVoidResult> ProcessOfflineVoidAsync(OfflineVoidRequest request);
}

/// <summary>
/// Secure offline-first transaction void service.
/// Enforces cryptographic local supervisor PIN verification, credential freshness, terminal binding,
/// atomic SQLite persistence, cash vs. non-cash refund separation, and durable outbox queuing.
/// </summary>
public class OfflineVoidService(PosSessionService session) : IOfflineVoidService
{
    private readonly PosSessionService _session = session;

    /// <summary>
    /// Maximum age of locally cached supervisor credentials before requiring an online refresh.
    /// </summary>
    public TimeSpan MaxOfflineSupervisorAuthAge { get; set; } = TimeSpan.FromDays(30);

    public async Task<bool> HasAuthorizedLocalSupervisorAsync()
    {
        using var db = new LocalPosDbContext();
        db.InitializeDatabase();
        var cashiers = await db.Cashiers.AsNoTracking().ToListAsync();
        return cashiers.Any(c =>
            !string.IsNullOrWhiteSpace(c.PinHash) &&
            PosPermissions.CanApproveVoid(c.Role));
    }

    public async Task<OfflineVoidResult> ProcessOfflineVoidAsync(OfflineVoidRequest request)
    {
        if (request == null)
            return OfflineVoidResult.Fail("Invalid void request.");

        if (!_session.IsAuthenticated || _session.Terminal == null)
            return OfflineVoidResult.Fail("An authenticated terminal session is required to void a transaction.");

        if (string.IsNullOrWhiteSpace(request.VoidReason))
            return OfflineVoidResult.Fail("Please provide a void reason.");

        if (string.IsNullOrWhiteSpace(request.SupervisorPin))
            return OfflineVoidResult.Fail("Supervisor PIN is required to authorize an offline void.");

        int activeTerminalId = _session.Terminal.TerminalId;
        int activeBranchId = _session.Terminal.BranchId;
        int activeOutletId = _session.Terminal.OutletId;
        string requestedBy = !string.IsNullOrWhiteSpace(_session.Username)
            ? _session.Username.Trim()
            : "cashier";

        await LocalPosDbContext.WriteLock.WaitAsync();
        try
        {
            using var db = new LocalPosDbContext();
            db.InitializeDatabase();

            var localCashiers = await db.Cashiers.AsNoTracking().ToListAsync();
            var authorizedSupervisors = localCashiers
                .Where(c => !string.IsNullOrWhiteSpace(c.PinHash) && PosPermissions.CanApproveVoid(c.Role))
                .ToList();

            if (authorizedSupervisors.Count == 0)
                return OfflineVoidResult.Fail(VoidAuthorizationHelper.NoAuthorizedSupervisorMessage);

            string enteredPinHash = VoidAuthorizationHelper.ComputePinHash(request.SupervisorPin);
            var matchedUser = localCashiers.FirstOrDefault(c =>
                VoidAuthorizationHelper.FixedTimeEqualsHex(c.PinHash, enteredPinHash));

            if (matchedUser == null)
                return OfflineVoidResult.Fail("Supervisor PIN not found or invalid.");

            if (!PosPermissions.CanApproveVoid(matchedUser.Role))
                return OfflineVoidResult.Fail(
                    $"User '{matchedUser.Username}' has role '{matchedUser.Role}', which does not have authorization to approve voids.");

            var terminalConfig = await db.TerminalConfigs.AsNoTracking()
                .FirstOrDefaultAsync(t => t.TerminalId == activeTerminalId);

            DateTime? credentialSyncedAtUtc = matchedUser.SyncedAtUtc ?? terminalConfig?.LastSyncAt;
            DateTime nowUtc = BusinessDayClock.UtcNow();
            if (credentialSyncedAtUtc.HasValue && MaxOfflineSupervisorAuthAge > TimeSpan.Zero)
            {
                var syncedUtc = BusinessDayClock.NormalizeUtc(credentialSyncedAtUtc.Value);
                if ((nowUtc - syncedUtc) > MaxOfflineSupervisorAuthAge)
                {
                    return OfflineVoidResult.Fail(
                        "Cached supervisor authorization credentials have expired. Please reconnect and refresh terminal data before voiding.");
                }
            }

            using var tx = await db.Database.BeginTransactionAsync();
            try
            {
                LocalOrder? localOrder = null;
                if (!string.IsNullOrWhiteSpace(request.LocalOrderId))
                {
                    var localId = request.LocalOrderId.Trim();
                    localOrder = await db.Orders.FirstOrDefaultAsync(o => o.OrderId == localId);
                }

                if (localOrder == null && !string.IsNullOrWhiteSpace(request.InvoiceNumber))
                {
                    var inv = request.InvoiceNumber.Trim();
                    localOrder = await db.Orders.FirstOrDefaultAsync(o => o.InvoiceNumber == inv);
                }

                if (localOrder == null && !string.IsNullOrWhiteSpace(request.OrderNumber))
                {
                    var ordNum = request.OrderNumber.Trim();
                    localOrder = await db.Orders.FirstOrDefaultAsync(o => o.OrderNumber == ordNum);
                }

                var snapshot = request.OrderSnapshot;
                if (localOrder == null && snapshot == null)
                {
                    await tx.RollbackAsync();
                    return OfflineVoidResult.Fail("Original transaction record was not found locally on this terminal.");
                }

                int targetBranchId = localOrder?.BranchId ?? snapshot!.BranchId;
                int targetTerminalId = localOrder?.TerminalId ?? snapshot!.TerminalId;
                int targetOutletId = localOrder?.OutletId ?? activeOutletId;

                if (targetBranchId != activeBranchId || targetTerminalId != activeTerminalId)
                {
                    await tx.RollbackAsync();
                    return OfflineVoidResult.Fail(
                        "Offline void denied: transaction does not belong to the active branch and terminal.");
                }

                string? targetStatus = localOrder?.OrderStatus ?? snapshot?.OrderStatus;
                if (string.Equals(targetStatus, "VOIDED", StringComparison.OrdinalIgnoreCase))
                {
                    await tx.RollbackAsync();
                    return OfflineVoidResult.Fail("Transaction is already voided.");
                }

                string? invoiceNumber = !string.IsNullOrWhiteSpace(localOrder?.InvoiceNumber)
                    ? localOrder.InvoiceNumber.Trim()
                    : snapshot?.InvoiceNumber?.Trim();
                string? orderNumber = !string.IsNullOrWhiteSpace(localOrder?.OrderNumber)
                    ? localOrder.OrderNumber.Trim()
                    : snapshot?.OrderNumber?.Trim();
                long? serverOrderId = request.ServerOrderId is > 0
                    ? request.ServerOrderId
                    : (snapshot?.OrderId is > 0 ? snapshot.OrderId : null);

                bool alreadyVoidedLocally = await db.TransactionVoids.AsNoTracking().AnyAsync(v =>
                    v.BranchId == activeBranchId &&
                    v.TerminalId == activeTerminalId &&
                    ((localOrder != null && v.LocalOrderId == localOrder.OrderId) ||
                     (serverOrderId.HasValue && v.ServerOrderId == serverOrderId.Value) ||
                     (!string.IsNullOrWhiteSpace(invoiceNumber) && v.OriginalInvoiceNumber == invoiceNumber)));

                if (alreadyVoidedLocally)
                {
                    await tx.RollbackAsync();
                    return OfflineVoidResult.Fail("Transaction has already been voided locally.");
                }

                DateTime voidedAtLocal = BusinessDayClock.DeviceNow();
                DateTime voidedAtUtc = BusinessDayClock.UtcNow();
                string clientVoidId = Guid.NewGuid().ToString("N");
                string idempotencyKey = $"VOID-{clientVoidId}";
                string reversalReference = "VOID-" + clientVoidId[..8].ToUpperInvariant();
                string deviceIdentifier = !string.IsNullOrWhiteSpace(terminalConfig?.TerminalIdentifier)
                    ? terminalConfig.TerminalIdentifier.Trim()
                    : (!string.IsNullOrWhiteSpace(_session.Terminal.DeviceIdentifier)
                        ? _session.Terminal.DeviceIdentifier.Trim()
                        : $"TERM-{activeTerminalId}");

                var refundBreakdowns = new List<OfflineVoidRefundBreakdownDto>();
                decimal cashRefundTotal = 0m;
                decimal nonCashPendingTotal = 0m;
                decimal totalVoidedAmount = localOrder?.TotalAmount ?? snapshot?.TotalAmount ?? 0m;

                if (localOrder != null)
                {
                    localOrder.OrderStatus = "VOIDED";
                    localOrder.PaymentStatus = "VOIDED";

                    var localPayments = await db.Payments
                        .Where(p => p.OrderId == localOrder.OrderId)
                        .ToListAsync();

                    foreach (var payment in localPayments.Where(p =>
                        string.Equals(p.PaymentStatus, PaymentStatuses.Paid, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(p.PaymentStatus, "SUCCESS", StringComparison.OrdinalIgnoreCase)))
                    {
                        payment.PaymentStatus = "REFUNDED";
                        bool isCash = string.Equals(payment.PaymentMethod, "CASH", StringComparison.OrdinalIgnoreCase);
                        if (isCash)
                            cashRefundTotal += payment.Amount;
                        else
                            nonCashPendingTotal += payment.Amount;

                        refundBreakdowns.Add(new OfflineVoidRefundBreakdownDto
                        {
                            PaymentMethod = payment.PaymentMethod,
                            Amount = payment.Amount,
                            RefundStatus = isCash ? "COMPLETED" : "PENDING_MANUAL_REFUND",
                            OriginalReferenceNumber = payment.ReferenceNumber
                        });
                    }
                }
                else if (snapshot?.Payments != null)
                {
                    foreach (var payment in snapshot.Payments.Where(p =>
                        string.Equals(p.PaymentStatus, PaymentStatuses.Paid, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(p.PaymentStatus, "SUCCESS", StringComparison.OrdinalIgnoreCase)))
                    {
                        bool isCash = string.Equals(payment.PaymentMethod, "CASH", StringComparison.OrdinalIgnoreCase);
                        if (isCash)
                            cashRefundTotal += payment.Amount;
                        else
                            nonCashPendingTotal += payment.Amount;

                        refundBreakdowns.Add(new OfflineVoidRefundBreakdownDto
                        {
                            PaymentMethod = payment.PaymentMethod,
                            Amount = payment.Amount,
                            RefundStatus = isCash ? "COMPLETED" : "PENDING_MANUAL_REFUND"
                        });
                    }
                }

                long? shiftId = localOrder?.ShiftId;
                string? clientShiftId = localOrder?.ClientShiftId;

                // Only CASH portions impact the physical cash drawer expected balance.
                if (cashRefundTotal > 0m)
                {
                    LocalCashDrawerShift? targetShift = null;
                    if (shiftId.HasValue && shiftId.Value != 0)
                    {
                        targetShift = await db.CashDrawerShifts.FirstOrDefaultAsync(s => s.ShiftId == shiftId.Value);
                    }
                    if (targetShift == null && !string.IsNullOrWhiteSpace(clientShiftId))
                    {
                        targetShift = await db.CashDrawerShifts.FirstOrDefaultAsync(s => s.ClientShiftId == clientShiftId);
                    }
                    if (targetShift == null)
                    {
                        targetShift = await db.CashDrawerShifts
                            .Where(s => s.TerminalId == activeTerminalId && s.Status == "OPEN")
                            .OrderByDescending(s => s.OpenedAt)
                            .FirstOrDefaultAsync();
                    }

                    if (targetShift != null)
                    {
                        shiftId ??= targetShift.ShiftId;
                        if (string.IsNullOrWhiteSpace(clientShiftId))
                            clientShiftId = targetShift.ClientShiftId;

                        targetShift.TotalCashRefunds += cashRefundTotal;
                        targetShift.ExpectedBalance =
                            targetShift.OpeningFloat
                            + targetShift.TotalCashSales
                            - targetShift.TotalCashRefunds
                            + targetShift.TotalCashIn
                            - targetShift.TotalCashOut
                            - targetShift.TotalCashDrops;
                    }
                }

                string refKey = !string.IsNullOrWhiteSpace(invoiceNumber)
                    ? invoiceNumber
                    : (orderNumber ?? localOrder?.OrderId ?? serverOrderId?.ToString() ?? string.Empty);

                string proofHash = VoidAuthorizationHelper.ComputeAuthorizationProofHash(
                    clientVoidId,
                    refKey,
                    activeBranchId,
                    activeTerminalId,
                    deviceIdentifier,
                    matchedUser.Username,
                    matchedUser.PinHash,
                    voidedAtUtc);

                var syncRequest = new OfflineVoidSyncRequest
                {
                    ClientVoidId = clientVoidId,
                    IdempotencyKey = idempotencyKey,
                    LocalTransactionId = localOrder?.OrderId,
                    OriginalOrderId = serverOrderId,
                    OriginalInvoiceNumber = invoiceNumber,
                    OriginalOrderNumber = orderNumber,
                    BranchId = activeBranchId,
                    OutletId = targetOutletId,
                    TerminalId = activeTerminalId,
                    DeviceIdentifier = deviceIdentifier,
                    ShiftId = shiftId,
                    ClientShiftId = clientShiftId,
                    VoidReason = request.VoidReason.Trim(),
                    RequestedBy = requestedBy,
                    ApprovedBy = matchedUser.Username,
                    SupervisorPinHash = matchedUser.PinHash,
                    AuthorizationProofHash = proofHash,
                    ReversalReference = reversalReference,
                    VoidedAtLocal = voidedAtLocal,
                    VoidedAtUtc = voidedAtUtc
                };

                var localVoid = new LocalTransactionVoid
                {
                    ClientVoidId = clientVoidId,
                    IdempotencyKey = idempotencyKey,
                    LocalOrderId = localOrder?.OrderId,
                    ServerOrderId = serverOrderId,
                    OriginalInvoiceNumber = invoiceNumber,
                    OriginalOrderNumber = orderNumber,
                    BranchId = activeBranchId,
                    OutletId = targetOutletId,
                    TerminalId = activeTerminalId,
                    DeviceIdentifier = deviceIdentifier,
                    ShiftId = shiftId,
                    ClientShiftId = clientShiftId,
                    VoidReason = request.VoidReason.Trim(),
                    RequestedBy = requestedBy,
                    ApprovedBy = matchedUser.Username,
                    ApprovedByRole = matchedUser.Role,
                    SupervisorPinHash = matchedUser.PinHash,
                    AuthorizationProofHash = proofHash,
                    ReversalReference = reversalReference,
                    TotalVoidedAmount = totalVoidedAmount,
                    CashRefundAmount = cashRefundTotal,
                    NonCashPendingRefundAmount = nonCashPendingTotal,
                    RefundBreakdownJson = JsonSerializer.Serialize(refundBreakdowns),
                    Status = "APPROVED_OFFLINE",
                    SyncStatus = "PENDING",
                    VoidedAtLocal = voidedAtLocal,
                    VoidedAtUtc = voidedAtUtc
                };

                db.TransactionVoids.Add(localVoid);
                db.SyncOutboxes.Add(new LocalSyncOutbox
                {
                    EntityType = "VOID",
                    EntityId = clientVoidId,
                    PayloadJson = JsonSerializer.Serialize(syncRequest),
                    Status = "PENDING",
                    CreatedAt = voidedAtUtc
                });
                db.SyncAuditLogs.Add(new LocalSyncAuditLog
                {
                    Message = $"[OFFLINE_VOID] ClientVoidId={clientVoidId} Invoice={invoiceNumber ?? orderNumber ?? "N/A"} RequestedBy={requestedBy} ApprovedBy={matchedUser.Username} ({matchedUser.Role}) CashRefund={cashRefundTotal:F2} NonCashPending={nonCashPendingTotal:F2} Device={deviceIdentifier} Reason=\"{request.VoidReason.Trim()}\"",
                    Timestamp = voidedAtUtc
                });

                await db.SaveChangesAsync();
                await tx.CommitAsync();

                LocalDatabaseBackupService.TriggerBackgroundSurvivableBackup();

                return new OfflineVoidResult
                {
                    Success = true,
                    Response = new VoidResponseDto
                    {
                        VoidId = 0,
                        ClientVoidId = clientVoidId,
                        IsOfflinePendingSync = true,
                        OriginalOrderId = serverOrderId ?? snapshot?.OrderId ?? 0,
                        OriginalInvoiceNumber = invoiceNumber,
                        VoidReason = localVoid.VoidReason,
                        RequestedBy = localVoid.RequestedBy,
                        ApprovedBy = localVoid.ApprovedBy,
                        Status = localVoid.Status,
                        ReversalReference = localVoid.ReversalReference,
                        VoidedAmount = localVoid.TotalVoidedAmount,
                        CashRefundAmount = localVoid.CashRefundAmount,
                        NonCashPendingManualRefundAmount = localVoid.NonCashPendingRefundAmount,
                        VoidedAt = localVoid.VoidedAtLocal
                    }
                };
            }
            catch
            {
                await tx.RollbackAsync();
                throw;
            }
        }
        finally
        {
            LocalPosDbContext.WriteLock.Release();
        }
    }
}

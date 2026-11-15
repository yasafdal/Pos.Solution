using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using POS.Client.Data;
using POS.Shared.Models;

namespace POS.Client.Services;

public interface IOfflineTransactionSyncService
{
    event Action? OnSyncStateChanged;
    Task<List<LocalSyncOutbox>> GetPendingOutboxItemsAsync();
    Task<int> ResetFailedOutboxItemsForRetryAsync();
    Task<bool> UploadTransactionAsync(LocalSyncOutbox outboxItem, HttpClient httpClient);
    Task<string> CheckServerStatusAsync(OfflineTransactionSyncRequest expected, HttpClient httpClient);
}

public class OfflineTransactionSyncService : IOfflineTransactionSyncService
{
    private readonly LocalPosDbContext _localDb;

    public event Action? OnSyncStateChanged;

    public OfflineTransactionSyncService(LocalPosDbContext localDb)
    {
        _localDb = localDb;
    }

    private static DateTime EnsureUtc(DateTime value) =>
        value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private static DateTime EnsureLocal(DateTime value) =>
        POS.Shared.Services.BusinessDayClock.NormalizeLocal(value);

    private static int GetEntitySyncPriority(string? entityType) =>
        entityType?.ToUpperInvariant() switch
        {
            "SHIFT_OPEN" => 0,
            "ORDER" => 1,
            "CASH_MOVEMENT" => 2,
            "SHIFT_CLOSE" => 3,
            _ => 4
        };

    public async Task<List<LocalSyncOutbox>> GetPendingOutboxItemsAsync()
    {
        using var db = new LocalPosDbContext();
        db.InitializeDatabase();
        var now = DateTime.UtcNow;
        var items = await db.SyncOutboxes
            .AsNoTracking()
            .Where(o => (o.Status == "PENDING" || o.Status == "RETRY_PENDING") && (o.NextRetryAt == null || o.NextRetryAt <= now))
            .ToListAsync();

        return items
            .OrderBy(o => GetEntitySyncPriority(o.EntityType))
            .ThenBy(o => o.CreatedAt)
            .ThenBy(o => o.OutboxId)
            .ToList();
    }

    public async Task<int> ResetFailedOutboxItemsForRetryAsync()
    {
        int resetCount = 0;
        await LocalPosDbContext.WriteLock.WaitAsync();
        try
        {
            using var db = new LocalPosDbContext();
            db.InitializeDatabase();
            var failedItems = await db.SyncOutboxes
                .Where(o => o.Status == "NEEDS_ATTENTION" || o.Status == "FAILED")
                .ToListAsync();

            if (failedItems.Count > 0)
            {
                var movementIds = new List<string>();
                foreach (var item in failedItems)
                {
                    item.Status = "RETRY_PENDING";
                    item.RetryCount = 0;
                    item.NextRetryAt = null;
                    if (string.Equals(item.EntityType, "CASH_MOVEMENT", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(item.EntityId))
                    {
                        movementIds.Add(item.EntityId);
                    }
                }

                if (movementIds.Count > 0)
                {
                    var movements = await db.CashDrawerMovements
                        .Where(m => movementIds.Contains(m.ClientMovementId) && (m.SyncStatus == "NEEDS_ATTENTION" || m.SyncStatus == "CONFLICT"))
                        .ToListAsync();
                    foreach (var m in movements)
                    {
                        m.SyncStatus = "PENDING";
                    }
                }

                resetCount = failedItems.Count;
                db.SyncAuditLogs.Add(new LocalSyncAuditLog
                {
                    Message = $"[SYNC_RETRY_RESET] Reset {resetCount} failed/needs-attention outbox record(s) to RETRY_PENDING for controlled retry.",
                    Timestamp = DateTime.UtcNow
                });
                await db.SaveChangesAsync();
            }
        }
        finally
        {
            LocalPosDbContext.WriteLock.Release();
        }

        if (resetCount > 0)
        {
            OnSyncStateChanged?.Invoke();
        }
        return resetCount;
    }

    private static string FormatDiagnosticLog(
        string entityType,
        string entityId,
        string operation,
        int attemptNumber,
        string endpoint,
        int? httpStatusCode,
        bool isRetryable,
        string? errorMessage)
    {
        string safeErr = (errorMessage ?? "Unknown error").Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (safeErr.Length > 350) safeErr = safeErr[..350];
        string codePart = httpStatusCode.HasValue ? httpStatusCode.Value.ToString() : "N/A";
        return $"[SYNC_DIAG] Entity={entityType} Id={entityId} Op={operation} Attempt={attemptNumber} Endpoint={endpoint} Status={codePart} Retryable={isRetryable} Error=\"{safeErr}\" Timestamp={DateTime.UtcNow:O}";
    }

    public async Task<bool> UploadTransactionAsync(LocalSyncOutbox outboxItem, HttpClient httpClient)
    {
        if (outboxItem.EntityType == "SHIFT_OPEN")
        {
            try
            {
                var opening = System.Text.Json.JsonSerializer.Deserialize<OpenShiftDto>(outboxItem.PayloadJson)
                    ?? throw new InvalidOperationException("Missing queued shift opening.");
                using (var ownerDb = new LocalPosDbContext())
                {
                    ownerDb.InitializeDatabase();
                    var owners = await ownerDb.CashDrawerShifts.AsNoTracking()
                        .Where(s => s.ClientShiftId == opening.ClientShiftId).ToListAsync();
                    if (string.IsNullOrWhiteSpace(opening.ClientShiftId) || outboxItem.EntityId != opening.ClientShiftId
                        || owners.Count == 0 || owners.Any(s => s.TerminalId != opening.TerminalId || s.BranchId != opening.BranchId
                            || string.IsNullOrWhiteSpace(s.CashierId)) || owners.Select(s => s.CashierId).Distinct().Count() != 1)
                    {
                        await UpdateOutboxStatusAsync(outboxItem.OutboxId, "NEEDS_ATTENTION", "Queued opening has no unambiguous local owner.", false, null,
                            FormatDiagnosticLog("SHIFT_OPEN", outboxItem.EntityId, "UPLOAD_SHIFT_OPEN", outboxItem.RetryCount + 1, "api/cash-drawer/open-shift", null, false, "Queued opening has no unambiguous local owner."));
                        return false;
                    }
                    var owner = owners[0].CashierId;
                    if (!string.IsNullOrWhiteSpace(opening.CashierId) && opening.CashierId != owner)
                    {
                        await UpdateOutboxStatusAsync(outboxItem.OutboxId, "NEEDS_ATTENTION", "Queued opening cashier does not match its original owner.", false, null,
                            FormatDiagnosticLog("SHIFT_OPEN", outboxItem.EntityId, "UPLOAD_SHIFT_OPEN", outboxItem.RetryCount + 1, "api/cash-drawer/open-shift", null, false, "Queued opening cashier does not match its original owner."));
                        return false;
                    }
                    opening.CashierId = owner;
                    opening.OpenedAt = opening.OpenedAt.HasValue
                        ? EnsureUtc(opening.OpenedAt.Value)
                        : (owners[0].OpenedAt != default ? EnsureUtc(owners[0].OpenedAt) : DateTime.UtcNow);
                }
                var response = await httpClient.PostAsJsonAsync("api/cash-drawer/open-shift", opening);
                if (!response.IsSuccessStatusCode)
                {
                    string errBody = await response.Content.ReadAsStringAsync();
                    int nextRetryCount = outboxItem.RetryCount + 1;
                    bool isPermanent = nextRetryCount >= 5 || OfflineRetryPolicy.IsPermanentFailureStatusCode((int)response.StatusCode);
                    string nextStatus = isPermanent ? "NEEDS_ATTENTION" : "RETRY_PENDING";
                    DateTime? nextRetryTime = isPermanent
                        ? null
                        : DateTime.UtcNow.Add(OfflineRetryPolicy.CalculateNextRetryDelay(nextRetryCount));

                    await UpdateOutboxStatusAsync(
                        outboxItem.OutboxId,
                        nextStatus,
                        errBody,
                        incrementRetry: true,
                        nextRetryAt: nextRetryTime,
                        FormatDiagnosticLog("SHIFT_OPEN", outboxItem.EntityId, "UPLOAD_SHIFT_OPEN", nextRetryCount, "api/cash-drawer/open-shift", (int)response.StatusCode, !isPermanent, errBody));
                    return false;
                }
                var shift = await response.Content.ReadFromJsonAsync<CashDrawerShiftDto>();
                if (shift == null || shift.ClientShiftId != opening.ClientShiftId || shift.TerminalId != opening.TerminalId
                    || shift.BranchId != opening.BranchId || shift.CashierId != opening.CashierId || shift.ShiftId <= 0
                    || !shift.OpeningConfirmed || shift.OpeningFloat != Math.Round(opening.OpeningFloat ?? -1, 2))
                {
                    await UpdateOutboxStatusAsync(outboxItem.OutboxId, "NEEDS_ATTENTION", "Shift acknowledgement does not match its original opening and owner.", false, null,
                        FormatDiagnosticLog("SHIFT_OPEN", outboxItem.EntityId, "UPLOAD_SHIFT_OPEN", outboxItem.RetryCount + 1, "api/cash-drawer/open-shift", (int)response.StatusCode, false, "Shift acknowledgement does not match its original opening and owner."));
                    return false;
                }
                await ShiftSession.CacheAsync(shift);
                await UpdateOutboxStatusAsync(outboxItem.OutboxId, "SYNCED", null, false, null,
                    $"Synchronized offline shift opening {opening.ClientShiftId} as Server Shift #{shift.ShiftId}");
                return true;
            }
            catch (Exception ex)
            {
                int nextRetryCount = outboxItem.RetryCount + 1;
                bool isPermanent = nextRetryCount >= 5;
                string nextStatus = isPermanent ? "NEEDS_ATTENTION" : "RETRY_PENDING";
                DateTime? nextRetryTime = isPermanent
                    ? null
                    : DateTime.UtcNow.Add(OfflineRetryPolicy.CalculateNextRetryDelay(nextRetryCount));

                await UpdateOutboxStatusAsync(
                    outboxItem.OutboxId,
                    nextStatus,
                    ex.Message,
                    incrementRetry: true,
                    nextRetryAt: nextRetryTime,
                    FormatDiagnosticLog("SHIFT_OPEN", outboxItem.EntityId, "UPLOAD_SHIFT_OPEN", nextRetryCount, "api/cash-drawer/open-shift", null, !isPermanent, ex.Message));
                return false;
            }
        }
        if (string.Equals(outboxItem.EntityType, "SHIFT_CLOSE", StringComparison.OrdinalIgnoreCase))
        {
            return await UploadShiftCloseAsync(outboxItem, httpClient);
        }
        if (string.Equals(outboxItem.EntityType, "CASH_MOVEMENT", StringComparison.OrdinalIgnoreCase))
        {
            return await UploadCashMovementAsync(outboxItem, httpClient);
        }

        try
        {
            LocalOrder? localOrder;
            List<LocalOrderItem> localItems;
            List<LocalPayment> localPayments;
            string deviceIdentifier;

            using (var readDb = new LocalPosDbContext())
            {
                readDb.InitializeDatabase();
                localOrder = await readDb.Orders
                    .AsNoTracking()
                    .FirstOrDefaultAsync(o => o.OrderId == outboxItem.EntityId);

                if (localOrder == null)
                {
                    await UpdateOutboxStatusAsync(outboxItem.OutboxId, "NEEDS_ATTENTION", "Local order record not found for outbox entity.", incrementRetry: false, nextRetryAt: null);
                    return false;
                }

                localItems = await readDb.OrderItems
                    .AsNoTracking()
                    .Where(i => i.OrderId == localOrder.OrderId)
                    .OrderBy(i => i.OrderItemId)
                    .ToListAsync();

                localPayments = await readDb.Payments
                    .AsNoTracking()
                    .Where(p => p.OrderId == localOrder.OrderId)
                    .ToListAsync();

                var outletConfig = await readDb.TerminalConfigs
                    .AsNoTracking()
                    .FirstOrDefaultAsync(o => o.TerminalId == localOrder.TerminalId);
                deviceIdentifier = outletConfig?.TerminalIdentifier ?? "TERM-01";
            }

            OrderCreateDto? payloadOrder = null;
            try
            {
                payloadOrder = POS.Shared.Services.LegacyTerminalPayload.Deserialize<OrderCreateDto>(outboxItem.PayloadJson);
            }
            catch
            {
                // Fallback to LocalOrderItems snapshot if payload deserialization fails
            }

            string? shiftClientId = !string.IsNullOrWhiteSpace(payloadOrder?.ClientShiftId)
                ? payloadOrder.ClientShiftId
                : localOrder.ClientShiftId;
            if (!string.IsNullOrEmpty(shiftClientId))
            {
                var (openingReady, openingBlocked) = await EnsureShiftOpeningSyncedAsync(shiftClientId, httpClient);
                if (!openingReady)
                {
                    await UpdateOutboxStatusAsync(outboxItem.OutboxId, "RETRY_PENDING",
                        openingBlocked ? "Opening shift requires attention before syncing sales." : "Waiting for opening shift acknowledgement.",
                        false, DateTime.UtcNow.AddMinutes(openingBlocked ? 5 : 1),
                        FormatDiagnosticLog("Order", localOrder.OrderId, "UPLOAD_TRANSACTION", outboxItem.RetryCount + 1, "api/offline-sync/transactions", null, !openingBlocked, "Waiting for opening shift acknowledgement."));
                    return false;
                }
            }

            long? resolvedShiftId = payloadOrder?.ShiftId > 0 ? payloadOrder.ShiftId : (localOrder.ShiftId > 0 ? localOrder.ShiftId : null);
            if (!resolvedShiftId.HasValue && !string.IsNullOrEmpty(shiftClientId))
            {
                using var shiftLookupDb = new LocalPosDbContext();
                shiftLookupDb.InitializeDatabase();
                var resolvedShift = await shiftLookupDb.CashDrawerShifts.AsNoTracking()
                    .Where(s => s.ClientShiftId == shiftClientId && s.ShiftId > 0)
                    .OrderByDescending(s => s.ShiftId)
                    .FirstOrDefaultAsync();
                if (resolvedShift != null)
                {
                    resolvedShiftId = resolvedShift.ShiftId;
                }
            }

            var preservedCreatedAtLocal = EnsureLocal(localOrder.CreatedAt);
            var preservedOffsetMinutes = localOrder.DeviceUtcOffsetMinutes
                ?? payloadOrder?.DeviceUtcOffsetMinutes
                ?? (int)Math.Round(POS.Shared.Services.BusinessDayClock.GetDeviceTimeZone().GetUtcOffset(preservedCreatedAtLocal).TotalMinutes);
            var preservedTimeZoneId = !string.IsNullOrWhiteSpace(localOrder.DeviceTimeZoneId)
                ? localOrder.DeviceTimeZoneId
                : (!string.IsNullOrWhiteSpace(payloadOrder?.DeviceTimeZoneId)
                    ? payloadOrder.DeviceTimeZoneId
                    : POS.Shared.Services.BusinessDayClock.GetDeviceTimeZoneId());
            var preservedTimestamp = payloadOrder?.TransactionTimestamp
                ?? new DateTimeOffset(preservedCreatedAtLocal, TimeSpan.FromMinutes(preservedOffsetMinutes));

            // Build payload mapping exact historical financial snapshots (including modifiers from payloadJson or ModifiersJson)
            var requestDto = new OfflineTransactionSyncRequest
            {
                LocalTransactionId = localOrder.OrderId,
                ShiftId = resolvedShiftId,
                ClientShiftId = shiftClientId,
                DeviceIdentifier = deviceIdentifier,
                BranchId = localOrder.BranchId,
                TerminalId = localOrder.TerminalId,
                CashierId = localOrder.CashierId,
                OrderNumber = !string.IsNullOrWhiteSpace(localOrder.OrderNumber) ? localOrder.OrderNumber : payloadOrder?.OfflineOrderNumber,
                InvoiceNumber = localOrder.InvoiceNumber,
                OrderType = localOrder.OrderType,
                TableNumber = localOrder.TableNumber,
                HeadCount = localOrder.HeadCount,
                TransactionDate = preservedCreatedAtLocal,
                TransactionTimestamp = preservedTimestamp,
                DeviceTimeZoneId = preservedTimeZoneId,
                DeviceUtcOffsetMinutes = preservedOffsetMinutes,
                ClientSentAtUtc = POS.Shared.Services.BusinessDayClock.DeviceNowOffset().UtcDateTime,
                SchemaVersion = "1.0",
                FinancialCalculationVersion = "1.0",
                ReferenceDataVersion = "1.0",
                IdempotencyKey = localOrder.OrderId,
                PayloadHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(outboxItem.PayloadJson ?? string.Empty))),
                Subtotal = localOrder.Subtotal,
                DiscountAmount = localOrder.DiscountAmount,
                DiscountCode = localOrder.DiscountCode,
                CustomerCardNumber = !string.IsNullOrWhiteSpace(localOrder.CustomerCardNumber) ? localOrder.CustomerCardNumber : payloadOrder?.CustomerCardNumber,
                CustomerName = !string.IsNullOrWhiteSpace(localOrder.CustomerName) ? localOrder.CustomerName : payloadOrder?.CustomerName,
                CustomerAddress = !string.IsNullOrWhiteSpace(localOrder.CustomerAddress) ? localOrder.CustomerAddress : payloadOrder?.CustomerAddress,
                CustomerTin = !string.IsNullOrWhiteSpace(localOrder.CustomerTin) ? localOrder.CustomerTin : payloadOrder?.CustomerTin,
                CustomerBusinessStyle = !string.IsNullOrWhiteSpace(localOrder.CustomerBusinessStyle) ? localOrder.CustomerBusinessStyle : payloadOrder?.CustomerBusinessStyle,
                VatableSales = localOrder.VatableSales,
                VatExemptSales = localOrder.VatExemptSales,
                ZeroRatedSales = localOrder.ZeroRatedSales,
                VatAmount = localOrder.VatAmount,
                TotalAmount = localOrder.TotalAmount,
                Items = localItems.Select((i, idx) =>
                {
                    var payloadItem = payloadOrder?.Items != null && idx < payloadOrder.Items.Count
                        ? payloadOrder.Items[idx]
                        : null;
                    List<OrderItemModifierCreateDto>? itemMods = payloadItem?.Modifiers;
                    if ((itemMods == null || itemMods.Count == 0) && !string.IsNullOrWhiteSpace(i.ModifiersJson))
                    {
                        try
                        {
                            itemMods = System.Text.Json.JsonSerializer.Deserialize<List<OrderItemModifierCreateDto>>(i.ModifiersJson);
                        }
                        catch { }
                    }
                    return new OfflineTransactionItemDto
                    {
                        ProductId = i.ProductId,
                        ProductName = i.ProductName,
                        Quantity = i.Quantity,
                        UnitPrice = i.UnitPrice,
                        LineTotal = i.LineTotal,
                        PricingMethod = i.PricingMethod ?? payloadItem?.PricingMethod,
                        IsComplimentary = i.IsComplimentary || (payloadItem?.IsComplimentary ?? false),
                        ParentProductId = i.ParentProductId ?? payloadItem?.ParentProductId,
                        TaxName = i.TaxName,
                        SpecialInstructions = CartItem.NormalizeSpecialInstructions(
                            !string.IsNullOrWhiteSpace(i.SpecialInstructions) ? i.SpecialInstructions : payloadItem?.SpecialInstructions),
                        Modifiers = itemMods?.Select(m => new OfflineModifierDto
                        {
                            OptionId = m.ModifierOptionId,
                            ProductId = m.ProductId,
                            Name = m.ModifierName ?? string.Empty,
                            AdditionalPrice = m.AdditionalPrice
                        }).ToList() ?? new()
                    };
                }).ToList(),
                Payments = localPayments.Select(p => new OfflinePaymentDto
                {
                    Amount = p.Amount,
                    TenderedAmount = p.TenderedAmount,
                    ChangeAmount = p.ChangeAmount,
                    PaymentMethod = p.PaymentMethod,
                    PaymentStatus = p.PaymentStatus,
                    ReferenceNumber = p.ReferenceNumber,
                    PaidAt = EnsureLocal(p.PaidAt)
                }).ToList()
            };

            // Mark outbox as UPLOADING using tracked entity under WriteLock
            await UpdateOutboxStatusAsync(outboxItem.OutboxId, "UPLOADING", null, incrementRetry: false, nextRetryAt: DateTime.UtcNow.AddMinutes(2));

            var response = await httpClient.PostAsJsonAsync("api/offline-sync/transactions", requestDto);
            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<OfflineTransactionSyncResponse>();
                if (POS.Shared.Services.OfflineSyncAcknowledgement.Matches(requestDto, result))
                {
                    var serverReceivedUtc = result?.ServerReceivedAtUtc ?? result?.AcknowledgedAt ?? POS.Shared.Services.BusinessDayClock.UtcNow();
                    string auditMsg = $"Successfully uploaded and acknowledged offline transaction {localOrder.OrderId} (Invoice: {localOrder.InvoiceNumber}, OccurredAtLocal: {preservedCreatedAtLocal:yyyy-MM-dd HH:mm:ss}, ServerReceivedAtUtc: {serverReceivedUtc:O})";
                    if (!string.IsNullOrWhiteSpace(result?.ClockSkewWarning))
                    {
                        auditMsg += $" | {result.ClockSkewWarning}";
                    }
                    await UpdateOutboxStatusAsync(
                        outboxItem.OutboxId,
                        "SYNCED",
                        null,
                        incrementRetry: false,
                        nextRetryAt: null,
                        auditMessage: auditMsg,
                        serverReceivedAtUtc: serverReceivedUtc);
                    return true;
                }
                else
                {
                    const string mismatchMsg = "Server response did not match expected transaction identity or invoice number.";
                    await UpdateOutboxStatusAsync(
                        outboxItem.OutboxId,
                        "NEEDS_ATTENTION",
                        mismatchMsg,
                        incrementRetry: false,
                        nextRetryAt: null,
                        auditMessage: FormatDiagnosticLog("Order", localOrder.OrderId, "UPLOAD_TRANSACTION", outboxItem.RetryCount + 1, "api/offline-sync/transactions", (int)response.StatusCode, false, mismatchMsg));
                    return false;
                }
            }

            if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
            {
                string conflictBody = await response.Content.ReadAsStringAsync();
                string conflictServerStatus = await CheckServerStatusAsync(requestDto, httpClient);
                if (conflictServerStatus == "SYNCED")
                {
                    await UpdateOutboxStatusAsync(
                        outboxItem.OutboxId,
                        "SYNCED",
                        null,
                        incrementRetry: false,
                        nextRetryAt: null,
                        auditMessage: $"Verified idempotent server persistence for transaction {localOrder.OrderId} (Invoice: {localOrder.InvoiceNumber}) after HTTP 409.");
                    return true;
                }

                await UpdateOutboxStatusAsync(
                    outboxItem.OutboxId,
                    "NEEDS_ATTENTION",
                    conflictBody,
                    incrementRetry: false,
                    nextRetryAt: null,
                    auditMessage: FormatDiagnosticLog("Order", localOrder.OrderId, "UPLOAD_TRANSACTION", outboxItem.RetryCount + 1, "api/offline-sync/transactions", (int)response.StatusCode, false, conflictBody));
                return false;
            }
            // Handle timeout or unknown state via status check endpoint
            string serverStatus = await CheckServerStatusAsync(requestDto, httpClient);
            if (serverStatus == "CONFLICT")
            {
                const string statusConflictMsg = "Server acknowledgement does not match this terminal/invoice/payload.";
                await UpdateOutboxStatusAsync(
                    outboxItem.OutboxId,
                    "NEEDS_ATTENTION",
                    statusConflictMsg,
                    incrementRetry: false,
                    nextRetryAt: null,
                    auditMessage: FormatDiagnosticLog("Order", localOrder.OrderId, "UPLOAD_TRANSACTION", outboxItem.RetryCount + 1, "api/offline-sync/transactions", (int)response.StatusCode, false, statusConflictMsg));
                return false;
            }
            if (serverStatus == "SYNCED")
            {
                await UpdateOutboxStatusAsync(outboxItem.OutboxId, "SYNCED", null, incrementRetry: false, nextRetryAt: null,
                    auditMessage: $"Verified server persistence via status endpoint for transaction {localOrder.OrderId} (Invoice: {localOrder.InvoiceNumber}).");
                return true;
            }

            int nextRetryCount = outboxItem.RetryCount + 1;
            bool isPermanent = nextRetryCount >= 5 || OfflineRetryPolicy.IsPermanentFailureStatusCode((int)response.StatusCode);
            string nextStatus = isPermanent ? "NEEDS_ATTENTION" : "RETRY_PENDING";
            DateTime? nextRetryTime = isPermanent ? null : DateTime.UtcNow.Add(OfflineRetryPolicy.CalculateNextRetryDelay(nextRetryCount));
            string errDetail = $"HTTP Upload failed. Status: {response.StatusCode}. Server Status: {serverStatus}";

            await UpdateOutboxStatusAsync(
                outboxItem.OutboxId,
                nextStatus,
                errDetail,
                incrementRetry: true,
                nextRetryAt: nextRetryTime,
                auditMessage: FormatDiagnosticLog("Order", localOrder.OrderId, "UPLOAD_TRANSACTION", nextRetryCount, "api/offline-sync/transactions", (int)response.StatusCode, !isPermanent, errDetail));
            return false;
        }
        catch (Exception ex)
        {
            int nextRetryCount = outboxItem.RetryCount + 1;
            bool isPermanent = nextRetryCount >= 5;
            string nextStatus = isPermanent ? "NEEDS_ATTENTION" : "RETRY_PENDING";
            DateTime? nextRetryTime = isPermanent ? null : DateTime.UtcNow.Add(OfflineRetryPolicy.CalculateNextRetryDelay(nextRetryCount));

            await UpdateOutboxStatusAsync(
                outboxItem.OutboxId,
                nextStatus,
                ex.Message,
                incrementRetry: true,
                nextRetryAt: nextRetryTime,
                auditMessage: FormatDiagnosticLog("Order", outboxItem.EntityId, "UPLOAD_TRANSACTION", nextRetryCount, "api/offline-sync/transactions", null, !isPermanent, ex.Message));
            return false;
        }
    }

    /// <summary>
    /// Ensures a prerequisite offline SHIFT_OPEN outbox item is synced without endlessly re-POSTing one already flagged NEEDS_ATTENTION.
    /// </summary>
    private async Task<(bool Ready, bool OpeningBlocked)> EnsureShiftOpeningSyncedAsync(string shiftClientId, HttpClient httpClient)
    {
        using var dependencyDb = new LocalPosDbContext();
        dependencyDb.InitializeDatabase();
        var pendingOpening = await dependencyDb.SyncOutboxes.AsNoTracking()
            .FirstOrDefaultAsync(o => o.EntityType == "SHIFT_OPEN" && o.EntityId == shiftClientId && o.Status != "SYNCED");
        if (pendingOpening == null)
            return (true, false);
        if (string.Equals(pendingOpening.Status, "NEEDS_ATTENTION", StringComparison.OrdinalIgnoreCase))
            return (false, true);

        bool synced = await UploadTransactionAsync(pendingOpening, httpClient);
        return (synced, false);
    }

    private async Task<bool> UploadShiftCloseAsync(LocalSyncOutbox outboxItem, HttpClient httpClient)
    {
        try
        {
            var closing = System.Text.Json.JsonSerializer.Deserialize<CloseShiftDto>(outboxItem.PayloadJson)
                ?? throw new InvalidOperationException("Missing queued shift closing payload.");

            if (!string.IsNullOrWhiteSpace(closing.ClientShiftId))
            {
                var (openingReady, openingBlocked) = await EnsureShiftOpeningSyncedAsync(closing.ClientShiftId, httpClient);
                if (!openingReady)
                {
                    await UpdateOutboxStatusAsync(
                        outboxItem.OutboxId,
                        "RETRY_PENDING",
                        openingBlocked ? "Opening shift requires attention before closing." : "Waiting for opening shift acknowledgement before closing.",
                        false,
                        DateTime.UtcNow.AddMinutes(openingBlocked ? 5 : 1));
                    return false;
                }
            }

            using (var ownerDb = new LocalPosDbContext())
            {
                ownerDb.InitializeDatabase();
                var localShift = await ownerDb.CashDrawerShifts.AsNoTracking()
                    .Where(s =>
                        (!string.IsNullOrWhiteSpace(closing.ClientCloseId) && s.ClientCloseId == closing.ClientCloseId)
                        || (!string.IsNullOrWhiteSpace(closing.ClientShiftId) && s.ClientShiftId == closing.ClientShiftId)
                        || (closing.ShiftId != 0 && s.ShiftId == closing.ShiftId))
                    .OrderByDescending(s => s.ShiftId)
                    .FirstOrDefaultAsync();

                if (localShift != null)
                {
                    if (localShift.ShiftId > 0) closing.ShiftId = localShift.ShiftId;
                    if (!string.IsNullOrWhiteSpace(localShift.ClientShiftId)) closing.ClientShiftId = localShift.ClientShiftId;
                    if (!string.IsNullOrWhiteSpace(localShift.CashierId)) closing.CashierId = localShift.CashierId;
                    closing.BranchId = localShift.BranchId;
                    closing.TerminalId = localShift.TerminalId;
                    if (!closing.ClosedAt.HasValue && localShift.ClosedAt.HasValue)
                    {
                        closing.ClosedAt = EnsureUtc(localShift.ClosedAt.Value);
                    }
                }
            }
            if (closing.ClosedAt.HasValue)
            {
                closing.ClosedAt = EnsureUtc(closing.ClosedAt.Value);
            }

            await UpdateOutboxStatusAsync(outboxItem.OutboxId, "UPLOADING", null, incrementRetry: false, nextRetryAt: DateTime.UtcNow.AddMinutes(2));

            var response = await httpClient.PostAsJsonAsync("api/cash-drawer/close-shift", closing);
            if (response.IsSuccessStatusCode)
            {
                var closedDto = await response.Content.ReadFromJsonAsync<CashDrawerShiftDto>();
                if (closedDto != null && closedDto.ShiftId > 0 && closedDto.Status == "CLOSED")
                {
                    await ShiftSession.CacheAsync(closedDto);
                    await UpdateOutboxStatusAsync(
                        outboxItem.OutboxId,
                        "SYNCED",
                        null,
                        incrementRetry: false,
                        nextRetryAt: null,
                        auditMessage: $"Synchronized offline shift closing {closing.ClientCloseId} for Shift #{closedDto.ShiftId}");
                    return true;
                }
            }

            if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
            {
                await UpdateOutboxStatusAsync(outboxItem.OutboxId, "NEEDS_ATTENTION", await response.Content.ReadAsStringAsync(), incrementRetry: false, nextRetryAt: null);
                return false;
            }

            int nextRetryCount = outboxItem.RetryCount + 1;
            bool isPermanent = nextRetryCount >= 5 || OfflineRetryPolicy.IsPermanentFailureStatusCode((int)response.StatusCode);
            string nextStatus = isPermanent ? "NEEDS_ATTENTION" : "RETRY_PENDING";
            DateTime? nextRetryTime = isPermanent ? null : DateTime.UtcNow.Add(OfflineRetryPolicy.CalculateNextRetryDelay(nextRetryCount));

            await UpdateOutboxStatusAsync(
                outboxItem.OutboxId,
                nextStatus,
                $"Shift close upload failed ({response.StatusCode}): {await response.Content.ReadAsStringAsync()}",
                incrementRetry: true,
                nextRetryAt: nextRetryTime);
            return false;
        }
        catch (Exception ex)
        {
            int nextRetryCount = outboxItem.RetryCount + 1;
            bool isPermanent = nextRetryCount >= 5;
            string nextStatus = isPermanent ? "NEEDS_ATTENTION" : "RETRY_PENDING";
            DateTime? nextRetryTime = isPermanent ? null : DateTime.UtcNow.Add(OfflineRetryPolicy.CalculateNextRetryDelay(nextRetryCount));
            await UpdateOutboxStatusAsync(outboxItem.OutboxId, nextStatus, ex.Message, incrementRetry: true, nextRetryAt: nextRetryTime);
            return false;
        }
    }

    private async Task UpdateOutboxStatusAsync(
        long outboxId,
        string status,
        string? lastError,
        bool incrementRetry,
        DateTime? nextRetryAt,
        string? auditMessage = null,
        DateTime? serverReceivedAtUtc = null)
    {
        await LocalPosDbContext.WriteLock.WaitAsync();
        try
        {
            using var db = new LocalPosDbContext();
            db.InitializeDatabase();
            var now = POS.Shared.Services.BusinessDayClock.UtcNow();
            var dbItem = await db.SyncOutboxes.FindAsync(outboxId);
            if (dbItem != null)
            {
                dbItem.Status = status;
                dbItem.LastError = lastError;
                if (incrementRetry)
                {
                    dbItem.RetryCount++;
                }
                dbItem.NextRetryAt = nextRetryAt;
                if (string.Equals(status, "SYNCED", StringComparison.OrdinalIgnoreCase))
                {
                    dbItem.SyncedAt = serverReceivedAtUtc ?? now;
                    var termCfg = await db.TerminalConfigs.FirstOrDefaultAsync();
                    if (termCfg != null)
                    {
                        termCfg.LastSyncAt = serverReceivedAtUtc ?? now;
                    }
                    if (!string.IsNullOrWhiteSpace(dbItem.EntityId))
                    {
                        var syncedOrder = await db.Orders.FirstOrDefaultAsync(o => o.OrderId == dbItem.EntityId);
                        if (syncedOrder != null)
                        {
                            // Record ServerReceivedAtUtc while strictly preserving original CreatedAt occurrence timestamp
                            syncedOrder.ServerReceivedAtUtc = serverReceivedAtUtc ?? now;
                        }
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(auditMessage))
            {
                db.SyncAuditLogs.Add(new LocalSyncAuditLog
                {
                    Message = auditMessage,
                    Timestamp = now
                });
            }

            await db.SaveChangesAsync();
        }
        finally
        {
            LocalPosDbContext.WriteLock.Release();
        }

        if (!string.Equals(status, "UPLOADING", StringComparison.OrdinalIgnoreCase))
        {
            OnSyncStateChanged?.Invoke();
        }
    }

    public async Task<string> CheckServerStatusAsync(OfflineTransactionSyncRequest expected, HttpClient httpClient)
    {
        try
        {
            var res = await httpClient.GetAsync($"api/offline-sync/transactions/{Uri.EscapeDataString(expected.LocalTransactionId)}/status");
            if (res.IsSuccessStatusCode)
            {
                var status = await res.Content.ReadFromJsonAsync<OfflineTransactionStatusDto>();
                return POS.Shared.Services.OfflineSyncAcknowledgement.Matches(expected, status) ? "SYNCED" : "CONFLICT";
            }
            return "UNKNOWN";
        }
        catch
        {
            return "UNREACHABLE";
        }
    }

    private async Task<bool> UploadCashMovementAsync(LocalSyncOutbox outboxItem, HttpClient httpClient)
    {
        try
        {
            LocalCashDrawerMovement? localMovement;
            LocalCashDrawerShift? localShift = null;
            using (var readDb = new LocalPosDbContext())
            {
                readDb.InitializeDatabase();
                localMovement = await readDb.CashDrawerMovements
                    .AsNoTracking()
                    .FirstOrDefaultAsync(m => m.ClientMovementId == outboxItem.EntityId);
                if (localMovement != null && localMovement.ShiftId != 0)
                {
                    localShift = await readDb.CashDrawerShifts
                        .AsNoTracking()
                        .FirstOrDefaultAsync(s => s.ShiftId == localMovement.ShiftId);
                }
            }

            if (localMovement == null)
            {
                await UpdateOutboxStatusAsync(outboxItem.OutboxId, "NEEDS_ATTENTION", "Local cash movement record not found for outbox entity.", incrementRetry: false, nextRetryAt: null);
                return false;
            }

            OfflineCashMovementSyncRequest? payloadRequest = null;
            if (!string.IsNullOrWhiteSpace(outboxItem.PayloadJson))
            {
                try { payloadRequest = System.Text.Json.JsonSerializer.Deserialize<OfflineCashMovementSyncRequest>(outboxItem.PayloadJson); }
                catch { payloadRequest = null; }
            }

            string? shiftClientId = !string.IsNullOrWhiteSpace(localShift?.ClientShiftId)
                ? localShift!.ClientShiftId
                : payloadRequest?.ClientShiftId;

            if (!string.IsNullOrWhiteSpace(shiftClientId))
            {
                var (openingReady, openingBlocked) = await EnsureShiftOpeningSyncedAsync(shiftClientId, httpClient);
                if (!openingReady)
                {
                    await UpdateOutboxStatusAsync(
                        outboxItem.OutboxId,
                        "RETRY_PENDING",
                        openingBlocked ? "Opening shift requires attention before syncing cash movement." : "Waiting for opening shift acknowledgement before syncing cash movement.",
                        incrementRetry: false,
                        nextRetryAt: DateTime.UtcNow.AddMinutes(openingBlocked ? 5 : 1),
                        auditMessage: FormatDiagnosticLog("CASH_MOVEMENT", localMovement.ClientMovementId, "UPLOAD_CASH_MOVEMENT", outboxItem.RetryCount + 1, "api/offline-sync/cash-movements", null, !openingBlocked, "Waiting for opening shift acknowledgement."));
                    return false;
                }
            }

            long resolvedShiftId = localMovement.ShiftId;
            if (resolvedShiftId <= 0 || string.IsNullOrWhiteSpace(shiftClientId))
            {
                using var shiftLookupDb = new LocalPosDbContext();
                shiftLookupDb.InitializeDatabase();

                var refreshedMovement = await shiftLookupDb.CashDrawerMovements
                    .AsNoTracking()
                    .FirstOrDefaultAsync(m => m.ClientMovementId == localMovement.ClientMovementId);
                if (refreshedMovement != null && refreshedMovement.ShiftId > 0)
                    resolvedShiftId = refreshedMovement.ShiftId;

                if (resolvedShiftId <= 0 && !string.IsNullOrWhiteSpace(shiftClientId))
                {
                    var resolvedShift = await shiftLookupDb.CashDrawerShifts.AsNoTracking()
                        .Where(s => s.ClientShiftId == shiftClientId && s.ShiftId > 0)
                        .OrderByDescending(s => s.ShiftId)
                        .FirstOrDefaultAsync();
                    if (resolvedShift != null)
                        resolvedShiftId = resolvedShift.ShiftId;
                }
                else if (resolvedShiftId > 0 && string.IsNullOrWhiteSpace(shiftClientId))
                {
                    var positiveShift = await shiftLookupDb.CashDrawerShifts.AsNoTracking()
                        .FirstOrDefaultAsync(s => s.ShiftId == resolvedShiftId);
                    shiftClientId = positiveShift?.ClientShiftId;
                }
            }

            var requestDto = new OfflineCashMovementSyncRequest
            {
                ClientMovementId = localMovement.ClientMovementId,
                ShiftId = resolvedShiftId,
                ClientShiftId = shiftClientId,
                BranchId = localMovement.BranchId,
                TerminalId = localMovement.TerminalId,
                MovementType = localMovement.MovementType,
                CategoryId = localMovement.CategoryId,
                CategoryName = localMovement.CategoryName,
                Amount = localMovement.Amount,
                Reason = localMovement.Reason,
                ReferenceNumber = localMovement.ReferenceNumber,
                Status = localMovement.Status,
                CreatedBy = localMovement.CreatedBy,
                ApprovedBy = localMovement.ApprovedBy,
                SupervisorPinHash = localMovement.SupervisorPinHash,
                CreatedAt = EnsureUtc(localMovement.CreatedAt),
                ApprovedAt = localMovement.ApprovedAt.HasValue ? EnsureUtc(localMovement.ApprovedAt.Value) : null,
                PostedAt = localMovement.PostedAt.HasValue ? EnsureUtc(localMovement.PostedAt.Value) : null
            };

            await UpdateOutboxStatusAsync(outboxItem.OutboxId, "UPLOADING", null, incrementRetry: false, nextRetryAt: DateTime.UtcNow.AddMinutes(2));

            var response = await httpClient.PostAsJsonAsync("api/offline-sync/cash-movements", requestDto);
            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<OfflineCashMovementSyncResponse>();
                if (result != null && result.Success)
                {
                    await UpdateCashMovementSyncStateAsync(localMovement.ClientMovementId, "SYNCED", result.ServerStatus, result.ReferenceNumber, null);
                    await UpdateOutboxStatusAsync(
                        outboxItem.OutboxId,
                        "SYNCED",
                        null,
                        incrementRetry: false,
                        nextRetryAt: null,
                        auditMessage: $"Synchronized offline cash movement {localMovement.ClientMovementId} ({result.ReferenceNumber}) as {result.ServerStatus}");
                    return true;
                }
            }
            else if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
            {
                string conflictMsg = await response.Content.ReadAsStringAsync();
                await UpdateCashMovementSyncStateAsync(localMovement.ClientMovementId, "CONFLICT", localMovement.Status, null, conflictMsg);
                await UpdateOutboxStatusAsync(
                    outboxItem.OutboxId,
                    "NEEDS_ATTENTION",
                    conflictMsg,
                    incrementRetry: true,
                    nextRetryAt: null,
                    auditMessage: $"Conflict syncing offline cash movement {localMovement.ClientMovementId}: {conflictMsg}");
                return false;
            }

            // Check server status in case of timeout after commit
            var statusRes = await httpClient.GetAsync($"api/offline-sync/cash-movements/{localMovement.ClientMovementId}/status");
            if (statusRes.IsSuccessStatusCode)
            {
                await UpdateCashMovementSyncStateAsync(localMovement.ClientMovementId, "SYNCED", null, null, null);
                await UpdateOutboxStatusAsync(outboxItem.OutboxId, "SYNCED", null, incrementRetry: false, nextRetryAt: null);
                return true;
            }

            int nextRetryCount = outboxItem.RetryCount + 1;
            bool isPermanent = nextRetryCount >= 5 || OfflineRetryPolicy.IsPermanentFailureStatusCode((int)response.StatusCode);
            string nextStatus = isPermanent ? "NEEDS_ATTENTION" : "RETRY_PENDING";
            DateTime? nextRetryTime = isPermanent ? null : DateTime.UtcNow.Add(OfflineRetryPolicy.CalculateNextRetryDelay(nextRetryCount));

            if (isPermanent)
            {
                await UpdateCashMovementSyncStateAsync(localMovement.ClientMovementId, "NEEDS_ATTENTION", null, null, $"Upload failed with HTTP {(int)response.StatusCode}");
            }

            await UpdateOutboxStatusAsync(
                outboxItem.OutboxId,
                nextStatus,
                $"Cash movement HTTP upload failed: {response.StatusCode}",
                incrementRetry: true,
                nextRetryAt: nextRetryTime);
            return false;
        }
        catch (Exception ex)
        {
            int nextRetryCount = outboxItem.RetryCount + 1;
            bool isPermanent = nextRetryCount >= 5;
            string nextStatus = isPermanent ? "NEEDS_ATTENTION" : "RETRY_PENDING";
            DateTime? nextRetryTime = isPermanent ? null : DateTime.UtcNow.Add(OfflineRetryPolicy.CalculateNextRetryDelay(nextRetryCount));

            await UpdateOutboxStatusAsync(outboxItem.OutboxId, nextStatus, ex.Message, incrementRetry: true, nextRetryAt: nextRetryTime);
            return false;
        }
    }

    private static async Task UpdateCashMovementSyncStateAsync(
        string clientMovementId,
        string syncStatus,
        string? serverStatus,
        string? serverReferenceNumber,
        string? syncError)
    {
        await LocalPosDbContext.WriteLock.WaitAsync();
        try
        {
            using var db = new LocalPosDbContext();
            db.InitializeDatabase();
            var m = await db.CashDrawerMovements.FirstOrDefaultAsync(x => x.ClientMovementId == clientMovementId);
            if (m != null)
            {
                m.SyncStatus = syncStatus;
                m.SyncError = syncError;
                if (!string.IsNullOrWhiteSpace(serverStatus))
                    m.Status = serverStatus;
                if (!string.IsNullOrWhiteSpace(serverReferenceNumber))
                    m.ReferenceNumber = serverReferenceNumber;
                await db.SaveChangesAsync();
            }
        }
        finally
        {
            LocalPosDbContext.WriteLock.Release();
        }
    }
}

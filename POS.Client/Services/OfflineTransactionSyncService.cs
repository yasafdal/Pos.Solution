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
            "VOID" => 2,
            "CASH_MOVEMENT" => 3,
            "SHIFT_UNVERIFIED_CASH" => 4,
            "SHIFT_CLOSE" or "SHIFT_OVERNIGHT_CLOSE" => 5,
            "CASHIER_REOPEN" => 6,
            _ => 7
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
                var voidIds = new List<string>();
                foreach (var item in failedItems)
                {
                    item.Status = "RETRY_PENDING";
                    item.RetryCount = 0;
                    item.NextRetryAt = null;
                    if (string.Equals(item.EntityType, "CASH_MOVEMENT", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(item.EntityId))
                    {
                        movementIds.Add(item.EntityId);
                    }
                    else if (string.Equals(item.EntityType, "VOID", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(item.EntityId))
                    {
                        voidIds.Add(item.EntityId);
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

                if (voidIds.Count > 0)
                {
                    var voids = await db.TransactionVoids
                        .Where(v => voidIds.Contains(v.ClientVoidId) && (v.SyncStatus == "NEEDS_ATTENTION" || v.SyncStatus == "CONFLICT"))
                        .ToListAsync();
                    foreach (var v in voids)
                    {
                        v.SyncStatus = "PENDING";
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
        // Guard against redundant replay if a causal prerequisite helper already synced this exact item in the same pass
        if (outboxItem.OutboxId > 0)
        {
            using var statusCheckDb = new LocalPosDbContext();
            statusCheckDb.InitializeDatabase();
            var dbRow = await statusCheckDb.SyncOutboxes.AsNoTracking()
                .Where(o => o.OutboxId == outboxItem.OutboxId)
                .Select(o => new { o.Status, o.PayloadJson })
                .FirstOrDefaultAsync();
            if (dbRow != null &&
                string.Equals(dbRow.Status, "SYNCED", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(dbRow.PayloadJson, outboxItem.PayloadJson, StringComparison.Ordinal))
                return true;
        }

        if (outboxItem.EntityType == "SHIFT_OPEN")
        {
            try
            {
                // Ensure any earlier shift closing (normal or supervisor overnight recovery) syncs before opening a newer shift
                if (outboxItem.OutboxId > 0)
                {
                    var (priorClosedReady, priorClosedBlocked) = await EnsureEarlierShiftClosingsSyncedAsync(outboxItem.OutboxId, httpClient);
                    if (!priorClosedReady)
                    {
                        await UpdateOutboxStatusAsync(
                            outboxItem.OutboxId,
                            "RETRY_PENDING",
                            priorClosedBlocked
                                ? "Earlier shift closing requires attention before opening the next shift."
                                : "Waiting for earlier shift closing to synchronize before opening the next shift.",
                            incrementRetry: false,
                            nextRetryAt: DateTime.UtcNow.AddMinutes(priorClosedBlocked ? 5 : 1));
                        return false;
                    }
                }

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
                        ? EnsureLocal(opening.OpenedAt.Value)
                        : (owners[0].OpenedAt != default ? EnsureLocal(owners[0].OpenedAt) : POS.Shared.Services.BusinessDayClock.ManilaNow());
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
        if (string.Equals(outboxItem.EntityType, "SHIFT_OVERNIGHT_CLOSE", StringComparison.OrdinalIgnoreCase))
        {
            return await UploadShiftOvernightCloseAsync(outboxItem, httpClient);
        }
        if (string.Equals(outboxItem.EntityType, "SHIFT_UNVERIFIED_CASH", StringComparison.OrdinalIgnoreCase))
        {
            return await UploadShiftUnverifiedCashAsync(outboxItem, httpClient);
        }
        if (string.Equals(outboxItem.EntityType, "CASHIER_REOPEN", StringComparison.OrdinalIgnoreCase))
        {
            return await UploadCashierReopenAsync(outboxItem, httpClient);
        }
        if (string.Equals(outboxItem.EntityType, "VOID", StringComparison.OrdinalIgnoreCase))
        {
            return await UploadVoidAsync(outboxItem, httpClient);
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
                    // If the sale was subsequently voided offline prior to upload, replay the original PAID event first
                    // so the queued VOID outbox record cleanly transitions it to VOIDED/REFUNDED on the server.
                    PaymentStatus = string.Equals(localOrder.OrderStatus, "VOIDED", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(p.PaymentStatus, "REFUNDED", StringComparison.OrdinalIgnoreCase)
                        ? POS.Shared.Constants.PaymentStatuses.Paid
                        : p.PaymentStatus,
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

    private async Task<(bool Ready, bool Blocked)> EnsureEarlierShiftClosingsSyncedAsync(long beforeOutboxId, HttpClient httpClient)
    {
        List<LocalSyncOutbox> earlierItems;
        using (var depDb = new LocalPosDbContext())
        {
            depDb.InitializeDatabase();
            bool hasEarlierClose = await depDb.SyncOutboxes.AsNoTracking()
                .AnyAsync(o => o.OutboxId < beforeOutboxId &&
                               o.Status != "SYNCED" &&
                               (o.EntityType == "SHIFT_CLOSE" || o.EntityType == "SHIFT_OVERNIGHT_CLOSE"));
            if (!hasEarlierClose)
                return (true, false);

            earlierItems = await depDb.SyncOutboxes.AsNoTracking()
                .Where(o => o.OutboxId < beforeOutboxId && o.Status != "SYNCED")
                .OrderBy(o => o.OutboxId)
                .ToListAsync();
        }

        if (earlierItems.Any(i => string.Equals(i.Status, "NEEDS_ATTENTION", StringComparison.OrdinalIgnoreCase)))
            return (false, true);

        foreach (var item in earlierItems)
        {
            if (!await UploadTransactionAsync(item, httpClient))
                return (false, false);
        }
        return (true, false);
    }

    private async Task<(bool Ready, bool Blocked)> EnsureEarlierShiftActivitySyncedAsync(long beforeOutboxId, HttpClient httpClient)
    {
        List<LocalSyncOutbox> earlierActivity;
        using (var depDb = new LocalPosDbContext())
        {
            depDb.InitializeDatabase();
            earlierActivity = await depDb.SyncOutboxes.AsNoTracking()
                .Where(o => o.OutboxId < beforeOutboxId &&
                            o.Status != "SYNCED" &&
                            (o.EntityType == "Order" || o.EntityType == "VOID" || o.EntityType == "CASH_MOVEMENT" || o.EntityType == "SHIFT_UNVERIFIED_CASH"))
                .OrderBy(o => o.OutboxId)
                .ToListAsync();
        }

        if (earlierActivity.Count == 0)
            return (true, false);
        if (earlierActivity.Any(i => string.Equals(i.Status, "NEEDS_ATTENTION", StringComparison.OrdinalIgnoreCase)))
            return (false, true);

        foreach (var item in earlierActivity)
        {
            if (!await UploadTransactionAsync(item, httpClient))
                return (false, false);
        }
        return (true, false);
    }

    private async Task<bool> UploadShiftOvernightCloseAsync(LocalSyncOutbox outboxItem, HttpClient httpClient)
    {
        try
        {
            var req = System.Text.Json.JsonSerializer.Deserialize<SupervisorOvernightCloseRequestDto>(outboxItem.PayloadJson)
                ?? throw new InvalidOperationException("Missing queued supervisor overnight close payload.");

            if (!string.IsNullOrWhiteSpace(req.ClientShiftId))
            {
                var (openingReady, openingBlocked) = await EnsureShiftOpeningSyncedAsync(req.ClientShiftId, httpClient);
                if (!openingReady)
                {
                    await UpdateOutboxStatusAsync(
                        outboxItem.OutboxId,
                        "RETRY_PENDING",
                        openingBlocked ? "Opening shift requires attention before overnight recovery closing." : "Waiting for opening shift acknowledgement before overnight recovery closing.",
                        false,
                        DateTime.UtcNow.AddMinutes(openingBlocked ? 5 : 1));
                    return false;
                }
            }

            if (outboxItem.OutboxId > 0)
            {
                var (activityReady, activityBlocked) = await EnsureEarlierShiftActivitySyncedAsync(outboxItem.OutboxId, httpClient);
                if (!activityReady)
                {
                    await UpdateOutboxStatusAsync(
                        outboxItem.OutboxId,
                        "RETRY_PENDING",
                        activityBlocked ? "Earlier shift transactions require attention before overnight recovery closing." : "Waiting for earlier shift transactions to sync before overnight recovery closing.",
                        false,
                        DateTime.UtcNow.AddMinutes(activityBlocked ? 5 : 1));
                    return false;
                }
            }

            using (var ownerDb = new LocalPosDbContext())
            {
                ownerDb.InitializeDatabase();
                var localShift = await ownerDb.CashDrawerShifts.AsNoTracking()
                    .Where(s =>
                        (!string.IsNullOrWhiteSpace(req.ClientOperationId) && (s.RecoveryOperationId == req.ClientOperationId || s.ClientCloseId == req.ClientOperationId))
                        || (!string.IsNullOrWhiteSpace(req.ClientShiftId) && s.ClientShiftId == req.ClientShiftId)
                        || (req.ShiftId != 0 && s.ShiftId == req.ShiftId))
                    .OrderByDescending(s => s.ShiftId)
                    .FirstOrDefaultAsync();

                if (localShift != null)
                {
                    if (localShift.ShiftId > 0) req.ShiftId = localShift.ShiftId;
                    if (!string.IsNullOrWhiteSpace(localShift.ClientShiftId)) req.ClientShiftId = localShift.ClientShiftId;
                    req.BranchId = localShift.BranchId;
                    req.TerminalId = localShift.TerminalId;
                    if (!req.ClosedAtUtc.HasValue && localShift.ClosedAt.HasValue)
                        req.ClosedAtUtc = POS.Shared.Services.BusinessDayClock.ManilaToUtc(localShift.ClosedAt.Value);
                }
            }
            if (req.ClosedAtUtc.HasValue)
                req.ClosedAtUtc = EnsureUtc(req.ClosedAtUtc.Value);

            await UpdateOutboxStatusAsync(outboxItem.OutboxId, "UPLOADING", null, incrementRetry: false, nextRetryAt: DateTime.UtcNow.AddMinutes(2));

            var response = await httpClient.PostAsJsonAsync("api/cash-drawer/overnight-recovery-close", req);
            if (response.IsSuccessStatusCode)
            {
                var closedDto = await response.Content.ReadFromJsonAsync<CashDrawerShiftDto>();
                if (closedDto != null && closedDto.ShiftId > 0 && closedDto.Status == "CLOSED")
                {
                    await ShiftSession.CacheAsync(closedDto);
                    await MarkLocalCashierRestrictionSyncedAsync(closedDto.TerminalId, closedDto.CashierId, req.ClientOperationId, closedDto.ShiftId);
                    await UpdateOutboxStatusAsync(
                        outboxItem.OutboxId,
                        "SYNCED",
                        null,
                        incrementRetry: false,
                        nextRetryAt: null,
                        auditMessage: $"Synchronized offline supervisor overnight recovery close {req.ClientOperationId} for Shift #{closedDto.ShiftId} (Original Business Date {closedDto.BusinessDate})");
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
                $"Supervisor overnight close upload failed ({response.StatusCode}): {await response.Content.ReadAsStringAsync()}",
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

    private async Task<bool> UploadShiftUnverifiedCashAsync(LocalSyncOutbox outboxItem, HttpClient httpClient)
    {
        try
        {
            var req = System.Text.Json.JsonSerializer.Deserialize<SupervisorUnverifiedCashRequestDto>(outboxItem.PayloadJson)
                ?? throw new InvalidOperationException("Missing queued unverified cash investigation payload.");

            if (!string.IsNullOrWhiteSpace(req.ClientShiftId))
            {
                var (openingReady, openingBlocked) = await EnsureShiftOpeningSyncedAsync(req.ClientShiftId, httpClient);
                if (!openingReady)
                {
                    await UpdateOutboxStatusAsync(
                        outboxItem.OutboxId,
                        "RETRY_PENDING",
                        openingBlocked ? "Opening shift requires attention before syncing unverified cash flag." : "Waiting for opening shift acknowledgement before syncing unverified cash flag.",
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
                        (!string.IsNullOrWhiteSpace(req.ClientShiftId) && s.ClientShiftId == req.ClientShiftId)
                        || (req.ShiftId != 0 && s.ShiftId == req.ShiftId))
                    .OrderByDescending(s => s.ShiftId)
                    .FirstOrDefaultAsync();
                if (localShift != null)
                {
                    if (localShift.ShiftId > 0) req.ShiftId = localShift.ShiftId;
                    if (!string.IsNullOrWhiteSpace(localShift.ClientShiftId)) req.ClientShiftId = localShift.ClientShiftId;
                    req.BranchId = localShift.BranchId;
                    req.TerminalId = localShift.TerminalId;
                }
            }
            if (req.ReportedAtUtc.HasValue)
                req.ReportedAtUtc = EnsureUtc(req.ReportedAtUtc.Value);

            await UpdateOutboxStatusAsync(outboxItem.OutboxId, "UPLOADING", null, incrementRetry: false, nextRetryAt: DateTime.UtcNow.AddMinutes(2));

            var response = await httpClient.PostAsJsonAsync("api/cash-drawer/overnight-recovery-unverified", req);
            if (response.IsSuccessStatusCode)
            {
                var dto = await response.Content.ReadFromJsonAsync<CashDrawerShiftDto>();
                if (dto != null && dto.ShiftId > 0)
                {
                    await ShiftSession.CacheAsync(dto);
                    await UpdateOutboxStatusAsync(
                        outboxItem.OutboxId,
                        "SYNCED",
                        null,
                        incrementRetry: false,
                        nextRetryAt: null,
                        auditMessage: $"Synchronized offline unverified-cash investigation flag {req.ClientOperationId} for Shift #{dto.ShiftId}");
                    return true;
                }
            }

            int nextRetryCount = outboxItem.RetryCount + 1;
            bool isPermanent = nextRetryCount >= 5 || OfflineRetryPolicy.IsPermanentFailureStatusCode((int)response.StatusCode);
            string nextStatus = isPermanent ? "NEEDS_ATTENTION" : "RETRY_PENDING";
            DateTime? nextRetryTime = isPermanent ? null : DateTime.UtcNow.Add(OfflineRetryPolicy.CalculateNextRetryDelay(nextRetryCount));

            await UpdateOutboxStatusAsync(
                outboxItem.OutboxId,
                nextStatus,
                $"Unverified cash investigation upload failed ({response.StatusCode}): {await response.Content.ReadAsStringAsync()}",
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
                        closing.ClosedAt = EnsureLocal(localShift.ClosedAt.Value);
                    }
                }
            }
            if (closing.ClosedAt.HasValue)
            {
                closing.ClosedAt = EnsureLocal(closing.ClosedAt.Value);
            }

            await UpdateOutboxStatusAsync(outboxItem.OutboxId, "UPLOADING", null, incrementRetry: false, nextRetryAt: DateTime.UtcNow.AddMinutes(2));

            var response = await httpClient.PostAsJsonAsync("api/cash-drawer/close-shift", closing);
            if (response.IsSuccessStatusCode)
            {
                var closedDto = await response.Content.ReadFromJsonAsync<CashDrawerShiftDto>();
                if (closedDto != null && closedDto.ShiftId > 0 && closedDto.Status == "CLOSED")
                {
                    await ShiftSession.CacheAsync(closedDto);
                    await MarkLocalCashierRestrictionSyncedAsync(closedDto.TerminalId, closedDto.CashierId, closing.ClientCloseId, closedDto.ShiftId);
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

    private static async Task MarkLocalCashierRestrictionSyncedAsync(int terminalId, string cashierId, string? clientCloseId, long? serverShiftId)
    {
        await LocalPosDbContext.WriteLock.WaitAsync();
        try
        {
            using var db = new LocalPosDbContext();
            db.InitializeDatabase();
            var rows = await db.CashierClosingRestrictions
                .Where(r => r.TerminalId == terminalId &&
                    ((!string.IsNullOrWhiteSpace(clientCloseId) && r.ClientCloseId == clientCloseId) ||
                     (r.CashierId.ToLower() == cashierId.Trim().ToLower() && r.SyncStatus == "PENDING" && r.Status == "ACTIVE")))
                .ToListAsync();
            foreach (var r in rows)
            {
                r.SyncStatus = "SYNCED";
                if (serverShiftId.HasValue && serverShiftId.Value > 0)
                    r.ShiftId = serverShiftId.Value;
            }
            if (rows.Count > 0)
                await db.SaveChangesAsync();
        }
        finally { LocalPosDbContext.WriteLock.Release(); }
    }

    private async Task<bool> UploadCashierReopenAsync(LocalSyncOutbox outboxItem, HttpClient httpClient)
    {
        try
        {
            var req = System.Text.Json.JsonSerializer.Deserialize<ReopenCashierRestrictionRequestDto>(outboxItem.PayloadJson)
                ?? throw new InvalidOperationException("Missing queued cashier reopen payload.");

            // Wait for any pending SHIFT_CLOSE for this cashier/terminal to sync first so the server restriction exists
            using (var depDb = new LocalPosDbContext())
            {
                depDb.InitializeDatabase();
                bool pendingClose = await depDb.SyncOutboxes.AsNoTracking()
                    .AnyAsync(o => o.EntityType == "SHIFT_CLOSE" && o.Status != "SYNCED");
                if (pendingClose)
                {
                    await UpdateOutboxStatusAsync(
                        outboxItem.OutboxId,
                        "RETRY_PENDING",
                        "Waiting for pending shift close to synchronize before syncing cashier reopen.",
                        incrementRetry: false,
                        nextRetryAt: DateTime.UtcNow.AddSeconds(30));
                    return false;
                }
            }

            await UpdateOutboxStatusAsync(outboxItem.OutboxId, "UPLOADING", null, incrementRetry: false, nextRetryAt: DateTime.UtcNow.AddMinutes(2));

            var response = await httpClient.PostAsJsonAsync("api/cash-drawer/cashier-restrictions/reopen", req);
            if (response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                await LocalPosDbContext.WriteLock.WaitAsync();
                try
                {
                    using var db = new LocalPosDbContext();
                    db.InitializeDatabase();
                    var rows = await db.CashierClosingRestrictions
                        .Where(r => r.TerminalId == req.TerminalId && r.ClientOperationId == req.ClientOperationId)
                        .ToListAsync();
                    foreach (var r in rows) r.SyncStatus = "SYNCED";
                    if (rows.Count > 0) await db.SaveChangesAsync();
                }
                finally { LocalPosDbContext.WriteLock.Release(); }

                await UpdateOutboxStatusAsync(
                    outboxItem.OutboxId,
                    "SYNCED",
                    null,
                    incrementRetry: false,
                    nextRetryAt: null,
                    auditMessage: $"Synchronized offline cashier reopen {req.ClientOperationId} for {req.CashierId} on Terminal #{req.TerminalId}");
                return true;
            }

            int nextRetryCount = outboxItem.RetryCount + 1;
            bool isPermanent = nextRetryCount >= 5 || OfflineRetryPolicy.IsPermanentFailureStatusCode((int)response.StatusCode);
            string nextStatus = isPermanent ? "NEEDS_ATTENTION" : "RETRY_PENDING";
            DateTime? nextRetryTime = isPermanent ? null : DateTime.UtcNow.Add(OfflineRetryPolicy.CalculateNextRetryDelay(nextRetryCount));
            await UpdateOutboxStatusAsync(
                outboxItem.OutboxId,
                nextStatus,
                $"Cashier reopen upload failed ({response.StatusCode}): {await response.Content.ReadAsStringAsync()}",
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
                CreatedAt = EnsureLocal(localMovement.CreatedAt),
                ApprovedAt = localMovement.ApprovedAt.HasValue ? EnsureLocal(localMovement.ApprovedAt.Value) : null,
                PostedAt = localMovement.PostedAt.HasValue ? EnsureLocal(localMovement.PostedAt.Value) : null
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

    private async Task<bool> UploadVoidAsync(LocalSyncOutbox outboxItem, HttpClient httpClient)
    {
        try
        {
            LocalTransactionVoid? localVoid;
            bool orderPendingSync = false;
            long? resolvedShiftId;

            using (var readDb = new LocalPosDbContext())
            {
                readDb.InitializeDatabase();
                localVoid = await readDb.TransactionVoids
                    .AsNoTracking()
                    .FirstOrDefaultAsync(v => v.ClientVoidId == outboxItem.EntityId);

                if (localVoid == null)
                {
                    await UpdateOutboxStatusAsync(
                        outboxItem.OutboxId,
                        "NEEDS_ATTENTION",
                        "Local void audit record not found for outbox entity.",
                        incrementRetry: false,
                        nextRetryAt: null);
                    return false;
                }

                if (!string.IsNullOrWhiteSpace(localVoid.LocalOrderId))
                {
                    orderPendingSync = await readDb.SyncOutboxes.AsNoTracking()
                        .AnyAsync(o =>
                            o.EntityType == "Order" &&
                            o.EntityId == localVoid.LocalOrderId &&
                            o.Status != "SYNCED");
                }

                resolvedShiftId = localVoid.ShiftId is > 0 ? localVoid.ShiftId : null;
                if (!resolvedShiftId.HasValue && !string.IsNullOrWhiteSpace(localVoid.ClientShiftId))
                {
                    var resolvedShift = await readDb.CashDrawerShifts.AsNoTracking()
                        .Where(s => s.ClientShiftId == localVoid.ClientShiftId && s.ShiftId > 0)
                        .OrderByDescending(s => s.ShiftId)
                        .FirstOrDefaultAsync();
                    if (resolvedShift != null)
                        resolvedShiftId = resolvedShift.ShiftId;
                }
            }

            if (orderPendingSync)
            {
                await UpdateOutboxStatusAsync(
                    outboxItem.OutboxId,
                    "RETRY_PENDING",
                    "Waiting for original offline order to synchronize before uploading void.",
                    incrementRetry: false,
                    nextRetryAt: DateTime.UtcNow.AddSeconds(30),
                    auditMessage: FormatDiagnosticLog("VOID", localVoid.ClientVoidId, "UPLOAD_VOID", outboxItem.RetryCount + 1, "api/offline-sync/voids", null, true, "Deferred until parent Order outbox item is SYNCED."));
                return false;
            }

            if (!string.IsNullOrWhiteSpace(localVoid.ClientShiftId))
            {
                var (openingReady, openingBlocked) = await EnsureShiftOpeningSyncedAsync(localVoid.ClientShiftId, httpClient);
                if (!openingReady)
                {
                    await UpdateOutboxStatusAsync(
                        outboxItem.OutboxId,
                        "RETRY_PENDING",
                        openingBlocked ? "Opening shift requires attention before syncing void." : "Waiting for opening shift acknowledgement before syncing void.",
                        incrementRetry: false,
                        nextRetryAt: DateTime.UtcNow.AddMinutes(openingBlocked ? 5 : 1),
                        auditMessage: FormatDiagnosticLog("VOID", localVoid.ClientVoidId, "UPLOAD_VOID", outboxItem.RetryCount + 1, "api/offline-sync/voids", null, !openingBlocked, "Waiting for opening shift acknowledgement."));
                    return false;
                }
            }

            OfflineVoidSyncRequest? payloadReq = null;
            if (!string.IsNullOrWhiteSpace(outboxItem.PayloadJson))
            {
                try { payloadReq = System.Text.Json.JsonSerializer.Deserialize<OfflineVoidSyncRequest>(outboxItem.PayloadJson); }
                catch { payloadReq = null; }
            }

            var requestDto = new OfflineVoidSyncRequest
            {
                ClientVoidId = localVoid.ClientVoidId,
                IdempotencyKey = string.IsNullOrWhiteSpace(localVoid.IdempotencyKey) ? $"VOID-{localVoid.ClientVoidId}" : localVoid.IdempotencyKey,
                LocalTransactionId = localVoid.LocalOrderId,
                OriginalOrderId = localVoid.ServerOrderId,
                OriginalInvoiceNumber = localVoid.OriginalInvoiceNumber,
                OriginalOrderNumber = localVoid.OriginalOrderNumber,
                BranchId = localVoid.BranchId,
                OutletId = localVoid.OutletId,
                TerminalId = localVoid.TerminalId,
                DeviceIdentifier = localVoid.DeviceIdentifier,
                ShiftId = resolvedShiftId ?? localVoid.ShiftId,
                ClientShiftId = localVoid.ClientShiftId,
                VoidReason = localVoid.VoidReason,
                RequestedBy = localVoid.RequestedBy,
                ApprovedBy = localVoid.ApprovedBy,
                SupervisorPinHash = !string.IsNullOrWhiteSpace(payloadReq?.SupervisorPinHash) ? payloadReq!.SupervisorPinHash : localVoid.SupervisorPinHash,
                AuthorizationProofHash = !string.IsNullOrWhiteSpace(payloadReq?.AuthorizationProofHash) ? payloadReq!.AuthorizationProofHash : localVoid.AuthorizationProofHash,
                ReversalReference = localVoid.ReversalReference,
                VoidedAtLocal = EnsureLocal(localVoid.VoidedAtLocal),
                VoidedAtUtc = EnsureUtc(localVoid.VoidedAtUtc)
            };

            await UpdateOutboxStatusAsync(outboxItem.OutboxId, "UPLOADING", null, incrementRetry: false, nextRetryAt: DateTime.UtcNow.AddMinutes(2));

            var response = await httpClient.PostAsJsonAsync("api/offline-sync/voids", requestDto);
            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<OfflineVoidSyncResponse>();
                if (result != null && result.Success)
                {
                    await UpdateTransactionVoidSyncStateAsync(
                        localVoid.ClientVoidId,
                        "SYNCED",
                        result.ServerStatus,
                        result.ServerVoidId,
                        result.ServerOrderId,
                        null);
                    await UpdateOutboxStatusAsync(
                        outboxItem.OutboxId,
                        "SYNCED",
                        null,
                        incrementRetry: false,
                        nextRetryAt: null,
                        auditMessage: $"Synchronized offline void {localVoid.ClientVoidId} ({localVoid.OriginalInvoiceNumber ?? localVoid.OriginalOrderNumber}) as Server Void #{result.ServerVoidId}");
                    return true;
                }
            }
            else if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
            {
                string conflictMsg = await response.Content.ReadAsStringAsync();
                // Preserve local audit trail and voided state; flag for supervisor review without silently un-voiding.
                await UpdateTransactionVoidSyncStateAsync(localVoid.ClientVoidId, "CONFLICT", localVoid.Status, null, null, conflictMsg);
                await UpdateOutboxStatusAsync(
                    outboxItem.OutboxId,
                    "NEEDS_ATTENTION",
                    conflictMsg,
                    incrementRetry: true,
                    nextRetryAt: null,
                    auditMessage: FormatDiagnosticLog("VOID", localVoid.ClientVoidId, "UPLOAD_VOID", outboxItem.RetryCount + 1, "api/offline-sync/voids", 409, false, conflictMsg));
                return false;
            }
            else if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                string notFoundMsg = await response.Content.ReadAsStringAsync();
                int retryCount = outboxItem.RetryCount + 1;
                bool exhausted = retryCount >= 5;
                string status = exhausted ? "NEEDS_ATTENTION" : "RETRY_PENDING";
                DateTime? retryAt = exhausted ? null : DateTime.UtcNow.Add(OfflineRetryPolicy.CalculateNextRetryDelay(retryCount));
                if (exhausted)
                {
                    await UpdateTransactionVoidSyncStateAsync(localVoid.ClientVoidId, "NEEDS_ATTENTION", null, null, null, notFoundMsg);
                }
                await UpdateOutboxStatusAsync(
                    outboxItem.OutboxId,
                    status,
                    string.IsNullOrWhiteSpace(notFoundMsg) ? "Original order not yet synced on server." : notFoundMsg,
                    incrementRetry: true,
                    nextRetryAt: retryAt,
                    auditMessage: FormatDiagnosticLog("VOID", localVoid.ClientVoidId, "UPLOAD_VOID", retryCount, "api/offline-sync/voids", 404, !exhausted, notFoundMsg));
                return false;
            }

            // Probe server idempotency status in case of timeout right after commit
            try
            {
                var statusRes = await httpClient.GetAsync($"api/offline-sync/voids/{localVoid.ClientVoidId}/status");
                if (statusRes.IsSuccessStatusCode)
                {
                    await UpdateTransactionVoidSyncStateAsync(localVoid.ClientVoidId, "SYNCED", "APPROVED", null, null, null);
                    await UpdateOutboxStatusAsync(outboxItem.OutboxId, "SYNCED", null, incrementRetry: false, nextRetryAt: null);
                    return true;
                }
            }
            catch { }

            int nextRetryCount = outboxItem.RetryCount + 1;
            bool isPermanent = nextRetryCount >= 5 || OfflineRetryPolicy.IsPermanentFailureStatusCode((int)response.StatusCode);
            string nextStatus = isPermanent ? "NEEDS_ATTENTION" : "RETRY_PENDING";
            DateTime? nextRetryTime = isPermanent ? null : DateTime.UtcNow.Add(OfflineRetryPolicy.CalculateNextRetryDelay(nextRetryCount));

            string errDetail = await response.Content.ReadAsStringAsync();
            if (isPermanent)
            {
                await UpdateTransactionVoidSyncStateAsync(localVoid.ClientVoidId, "NEEDS_ATTENTION", null, null, null, $"Upload failed with HTTP {(int)response.StatusCode}: {errDetail}");
            }

            await UpdateOutboxStatusAsync(
                outboxItem.OutboxId,
                nextStatus,
                $"Offline void HTTP upload failed: {response.StatusCode}",
                incrementRetry: true,
                nextRetryAt: nextRetryTime,
                auditMessage: FormatDiagnosticLog("VOID", localVoid.ClientVoidId, "UPLOAD_VOID", nextRetryCount, "api/offline-sync/voids", (int)response.StatusCode, !isPermanent, errDetail));
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
                auditMessage: FormatDiagnosticLog("VOID", outboxItem.EntityId, "UPLOAD_VOID", nextRetryCount, "api/offline-sync/voids", null, !isPermanent, ex.Message));
            return false;
        }
    }

    private static async Task UpdateTransactionVoidSyncStateAsync(
        string clientVoidId,
        string syncStatus,
        string? serverStatus,
        long? serverVoidId,
        long? serverOrderId,
        string? syncError)
    {
        await LocalPosDbContext.WriteLock.WaitAsync();
        try
        {
            using var db = new LocalPosDbContext();
            db.InitializeDatabase();
            var v = await db.TransactionVoids.FirstOrDefaultAsync(x => x.ClientVoidId == clientVoidId);
            if (v != null)
            {
                v.SyncStatus = syncStatus;
                v.SyncError = syncError;
                if (!string.IsNullOrWhiteSpace(serverStatus))
                    v.Status = serverStatus;
                if (serverVoidId.HasValue && serverVoidId.Value > 0)
                    v.ServerVoidId = serverVoidId.Value;
                if (serverOrderId.HasValue && serverOrderId.Value > 0)
                    v.ServerOrderId = serverOrderId.Value;
                await db.SaveChangesAsync();
            }
        }
        finally
        {
            LocalPosDbContext.WriteLock.Release();
        }
    }
}

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
    Task<List<LocalSyncOutbox>> GetPendingOutboxItemsAsync();
    Task<bool> UploadTransactionAsync(LocalSyncOutbox outboxItem, HttpClient httpClient);
    Task<string> CheckServerStatusAsync(OfflineTransactionSyncRequest expected, HttpClient httpClient);
}

public class OfflineTransactionSyncService : IOfflineTransactionSyncService
{
    private readonly LocalPosDbContext _localDb;

    public OfflineTransactionSyncService(LocalPosDbContext localDb)
    {
        _localDb = localDb;
    }

    public async Task<List<LocalSyncOutbox>> GetPendingOutboxItemsAsync()
    {
        using var db = new LocalPosDbContext();
        db.InitializeDatabase();
        var now = DateTime.UtcNow;
        return await db.SyncOutboxes
            .AsNoTracking()
            .Where(o => (o.Status == "PENDING" || o.Status == "RETRY_PENDING") && (o.NextRetryAt == null || o.NextRetryAt <= now))
            .OrderBy(o => o.CreatedAt)
            .ToListAsync();
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
                        await UpdateOutboxStatusAsync(outboxItem.OutboxId, "NEEDS_ATTENTION", "Queued opening has no unambiguous local owner.", false, null);
                        return false;
                    }
                    var owner = owners[0].CashierId;
                    if (!string.IsNullOrWhiteSpace(opening.CashierId) && opening.CashierId != owner)
                    {
                        await UpdateOutboxStatusAsync(outboxItem.OutboxId, "NEEDS_ATTENTION", "Queued opening cashier does not match its original owner.", false, null);
                        return false;
                    }
                    opening.CashierId = owner;
                }
                var response = await httpClient.PostAsJsonAsync("api/cash-drawer/open-shift", opening);
                if (!response.IsSuccessStatusCode) { await UpdateOutboxStatusAsync(outboxItem.OutboxId, "RETRY_PENDING", await response.Content.ReadAsStringAsync(), true, DateTime.UtcNow.AddMinutes(1)); return false; }
                var shift = await response.Content.ReadFromJsonAsync<CashDrawerShiftDto>();
                if (shift == null || shift.ClientShiftId != opening.ClientShiftId || shift.TerminalId != opening.TerminalId
                    || shift.BranchId != opening.BranchId || shift.CashierId != opening.CashierId || shift.ShiftId <= 0
                    || !shift.OpeningConfirmed || shift.OpeningFloat != Math.Round(opening.OpeningFloat ?? -1, 2))
                {
                    await UpdateOutboxStatusAsync(outboxItem.OutboxId, "NEEDS_ATTENTION", "Shift acknowledgement does not match its original opening and owner.", false, null);
                    return false;
                }
                await ShiftSession.CacheAsync(shift);
                await UpdateOutboxStatusAsync(outboxItem.OutboxId, "SYNCED", null, false, null);
                return true;
            }
            catch (Exception ex) { await UpdateOutboxStatusAsync(outboxItem.OutboxId, "RETRY_PENDING", ex.Message, true, DateTime.UtcNow.AddMinutes(1)); return false; }
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

            if (!string.IsNullOrEmpty(payloadOrder?.ClientShiftId))
            {
                using var dependencyDb = new LocalPosDbContext(); dependencyDb.InitializeDatabase();
                var pendingOpening = await dependencyDb.SyncOutboxes.AsNoTracking().FirstOrDefaultAsync(o => o.EntityType == "SHIFT_OPEN" && o.EntityId == payloadOrder.ClientShiftId && o.Status != "SYNCED");
                if (pendingOpening != null && !await UploadTransactionAsync(pendingOpening, httpClient))
                {
                    await UpdateOutboxStatusAsync(outboxItem.OutboxId, "RETRY_PENDING", "Waiting for opening shift acknowledgement.", false, DateTime.UtcNow.AddMinutes(1)); return false;
                }
            }
            // Build payload mapping exact historical financial snapshots (including modifiers from payloadJson if available)
            var requestDto = new OfflineTransactionSyncRequest
            {
                LocalTransactionId = localOrder.OrderId,
                ShiftId = payloadOrder?.ShiftId > 0 ? payloadOrder.ShiftId : null,
                ClientShiftId = payloadOrder?.ClientShiftId,
                DeviceIdentifier = deviceIdentifier,
                BranchId = localOrder.BranchId,
                TerminalId = localOrder.TerminalId,
                CashierId = localOrder.CashierId,
                InvoiceNumber = localOrder.InvoiceNumber,
                OrderType = localOrder.OrderType,
                TableNumber = localOrder.TableNumber,
                HeadCount = localOrder.HeadCount,
                TransactionDate = localOrder.CreatedAt,
                SchemaVersion = "1.0",
                FinancialCalculationVersion = "1.0",
                ReferenceDataVersion = "1.0",
                IdempotencyKey = localOrder.OrderId,
                PayloadHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(outboxItem.PayloadJson ?? string.Empty))),
                Subtotal = localOrder.Subtotal,
                DiscountAmount = localOrder.DiscountAmount,
                DiscountCode = localOrder.DiscountCode,
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
                    return new OfflineTransactionItemDto
                    {
                        ProductId = i.ProductId,
                        ProductName = i.ProductName,
                        Quantity = i.Quantity,
                        UnitPrice = i.UnitPrice,
                        LineTotal = i.LineTotal,
                        TaxName = i.TaxName,
                        Modifiers = payloadItem?.Modifiers?.Select(m => new OfflineModifierDto
                        {
                            OptionId = m.ModifierOptionId,
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
                    PaidAt = p.PaidAt
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
                    await UpdateOutboxStatusAsync(
                        outboxItem.OutboxId,
                        "SYNCED",
                        null,
                        incrementRetry: false,
                        nextRetryAt: null,
                        auditMessage: $"Successfully uploaded and acknowledged offline transaction {localOrder.OrderId} (Invoice: {localOrder.InvoiceNumber})");
                    return true;
                }
                else
                {
                    await UpdateOutboxStatusAsync(
                        outboxItem.OutboxId,
                        "NEEDS_ATTENTION",
                        "Server response did not match expected transaction identity or invoice number.",
                        incrementRetry: false,
                        nextRetryAt: null);
                    return false;
                }
            }

            if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
            {
                await UpdateOutboxStatusAsync(outboxItem.OutboxId, "NEEDS_ATTENTION", await response.Content.ReadAsStringAsync(), incrementRetry: false, nextRetryAt: null);
                return false;
            }
            // Handle timeout or unknown state via status check endpoint
            string serverStatus = await CheckServerStatusAsync(requestDto, httpClient);
            if (serverStatus == "CONFLICT")
            {
                await UpdateOutboxStatusAsync(outboxItem.OutboxId, "NEEDS_ATTENTION", "Server acknowledgement does not match this terminal/invoice/payload.", incrementRetry: false, nextRetryAt: null);
                return false;
            }
            if (serverStatus == "SYNCED")
            {
                await UpdateOutboxStatusAsync(outboxItem.OutboxId, "SYNCED", null, incrementRetry: false, nextRetryAt: null);
                return true;
            }

            int nextRetryCount = outboxItem.RetryCount + 1;
            bool isPermanent = nextRetryCount >= 5 || OfflineRetryPolicy.IsPermanentFailureStatusCode((int)response.StatusCode);
            string nextStatus = isPermanent ? "NEEDS_ATTENTION" : "RETRY_PENDING";
            DateTime? nextRetryTime = isPermanent ? null : DateTime.UtcNow.Add(OfflineRetryPolicy.CalculateNextRetryDelay(nextRetryCount));

            await UpdateOutboxStatusAsync(
                outboxItem.OutboxId,
                nextStatus,
                $"HTTP Upload failed. Status: {response.StatusCode}. Server Status: {serverStatus}",
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

    private static async Task UpdateOutboxStatusAsync(
        long outboxId,
        string status,
        string? lastError,
        bool incrementRetry,
        DateTime? nextRetryAt,
        string? auditMessage = null)
    {
        await LocalPosDbContext.WriteLock.WaitAsync();
        try
        {
            using var db = new LocalPosDbContext();
            db.InitializeDatabase();
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
            }

            if (!string.IsNullOrWhiteSpace(auditMessage))
            {
                db.SyncAuditLogs.Add(new LocalSyncAuditLog
                {
                    Message = auditMessage,
                    Timestamp = DateTime.UtcNow
                });
            }

            await db.SaveChangesAsync();
        }
        finally
        {
            LocalPosDbContext.WriteLock.Release();
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
            using (var readDb = new LocalPosDbContext())
            {
                readDb.InitializeDatabase();
                localMovement = await readDb.CashDrawerMovements
                    .AsNoTracking()
                    .FirstOrDefaultAsync(m => m.ClientMovementId == outboxItem.EntityId);
            }

            if (localMovement == null)
            {
                await UpdateOutboxStatusAsync(outboxItem.OutboxId, "NEEDS_ATTENTION", "Local cash movement record not found for outbox entity.", incrementRetry: false, nextRetryAt: null);
                return false;
            }

            var requestDto = new OfflineCashMovementSyncRequest
            {
                ClientMovementId = localMovement.ClientMovementId,
                ShiftId = localMovement.ShiftId,
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
                CreatedAt = localMovement.CreatedAt,
                ApprovedAt = localMovement.ApprovedAt,
                PostedAt = localMovement.PostedAt
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


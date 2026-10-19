using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using POS.Client.Data;
using POS.Shared.Models;
using POS.Shared.Services;

namespace POS.Client.Services;

public interface IOfflineSyncWorker
{
    Task<int> SyncPendingTransactionsAsync(HttpClient httpClient);
}

public class OfflineSyncWorker : IOfflineSyncWorker
{
    private readonly LocalPosDbContext _localDb;

    public OfflineSyncWorker(LocalPosDbContext localDb)
    {
        _localDb = localDb;
    }

    public async Task<int> SyncPendingTransactionsAsync(HttpClient httpClient)
    {
        var pendingOutboxItems = await _localDb.SyncOutboxes
            .Where(o => o.Status == "PENDING" && o.RetryCount < 5)
            .OrderBy(o => o.CreatedAt)
            .Take(20)
            .ToListAsync();

        int syncedCount = 0;

        foreach (var item in pendingOutboxItems)
        {
            try
            {
                var orderCreateDto = System.Text.Json.JsonSerializer.Deserialize<OrderCreateDto>(item.PayloadJson);
                if (orderCreateDto == null)
                {
                    item.Status = "FAILED";
                    item.LastError = "Invalid payload JSON serialization.";
                    item.RetryCount++;
                    continue;
                }

                // Call central API via offline-sync transactions endpoint using OfflineInvoiceNumber preservation
                orderCreateDto.OfflineInvoiceNumber = orderCreateDto.OfflineInvoiceNumber ?? item.EntityId; // Or parse from entity
                var syncReq = new OfflineTransactionSyncRequest
                {
                    LocalTransactionId = item.EntityId,
                    TerminalId = "TERM-01",
                    BranchId = orderCreateDto.BranchId,
                    OutletId = orderCreateDto.OutletId,
                    CashierId = orderCreateDto.CashierId,
                    InvoiceNumber = orderCreateDto.OfflineInvoiceNumber ?? $"OFF-{item.EntityId[..8]}",
                    TransactionDate = item.CreatedAt,
                    Subtotal = orderCreateDto.Items.Sum(i => i.BasePrice * i.Quantity),
                    DiscountAmount = orderCreateDto.DiscountAmount,
                    DiscountCode = orderCreateDto.DiscountCode,
                    TotalAmount = orderCreateDto.Items.Sum(i => i.BasePrice * i.Quantity) - orderCreateDto.DiscountAmount,
                    Items = orderCreateDto.Items.Select(i => new OfflineTransactionItemDto
                    {
                        ProductId = i.ProductId,
                        ProductName = i.ProductName ?? "Product",
                        Quantity = (int)i.Quantity,
                        UnitPrice = i.BasePrice,
                        LineTotal = i.BasePrice * i.Quantity,
                        TaxName = i.TaxName ?? "VATable"
                    }).ToList(),
                    Payments = new List<OfflinePaymentDto>
                    {
                        new() { Amount = orderCreateDto.Items.Sum(i => i.BasePrice * i.Quantity) - orderCreateDto.DiscountAmount, TenderedAmount = orderCreateDto.Items.Sum(i => i.BasePrice * i.Quantity) - orderCreateDto.DiscountAmount, ChangeAmount = 0m, PaymentMethod = "CASH", PaymentStatus = "SUCCESS", PaidAt = DateTime.UtcNow }
                    },
                    IdempotencyKey = item.EntityId,
                    PayloadHash = item.PayloadJson.GetHashCode().ToString()
                };

                var response = await httpClient.PostAsJsonAsync("api/offline-sync/transactions", syncReq);
                if (response.IsSuccessStatusCode)
                {
                    item.Status = "SYNCED";
                    item.LastError = null;
                    syncedCount++;

                    _localDb.SyncAuditLogs.Add(new LocalSyncAuditLog
                    {
                        Message = $"Successfully synced offline transaction {item.EntityId} at {DateTime.UtcNow}",
                        Timestamp = DateTime.UtcNow
                    });
                }
                else
                {
                    string errContent = await response.Content.ReadAsStringAsync();
                    item.RetryCount++;
                    item.LastError = $"HTTP {(int)response.StatusCode}: {errContent}";
                    if (item.RetryCount >= 5)
                    {
                        item.Status = "FAILED";
                    }
                }
            }
            catch (Exception ex)
            {
                item.RetryCount++;
                item.LastError = ex.Message;
                if (item.RetryCount >= 5)
                {
                    item.Status = "FAILED";
                }
            }
        }

        await _localDb.SaveChangesAsync();
        return syncedCount;
    }
}

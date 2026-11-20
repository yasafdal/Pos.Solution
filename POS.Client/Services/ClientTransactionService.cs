using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using POS.Client.Data;
using POS.Shared.Constants;
using POS.Shared.Interfaces;
using POS.Shared.Models;
using POS.Shared.Services;

namespace POS.Client.Services;

public interface IClientTransactionHistoryService : ITransactionService
{
    Task<List<OrderResponseDto>> GetLocalTransactionsAsync(TransactionFilterDto filter);
    Task ApplyLocalVoidOverlayAsync(List<OrderResponseDto> transactions);
}

/// <summary>
/// Permission-enforced client transaction history service.
/// Prevents unauthorized roles (such as Cashier) from retrieving historical sales records
/// via either POS.API or local SQLite storage, while leaving checkout and background sync unaffected.
/// </summary>
public class ClientTransactionService(HttpClient http, PosSessionService session)
    : IClientTransactionHistoryService
{
    private readonly TransactionService _apiService = new(http);
    private readonly PosSessionService _session = session;

    private void EnsureCanViewTransactionHistory()
    {
        if (!_session.IsAuthenticated || !_session.HasPermission(PosPermissions.ViewTransactionHistory))
        {
            throw new UnauthorizedAccessException(
                "Access denied: your role does not have permission to view Transaction History.");
        }
    }

    public Task<List<OrderResponseDto>> GetTransactionsAsync(TransactionFilterDto filter)
    {
        EnsureCanViewTransactionHistory();
        return _apiService.GetTransactionsAsync(filter);
    }

    public Task<OrderResponseDto?> GetTransactionByIdAsync(long id)
    {
        EnsureCanViewTransactionHistory();
        return _apiService.GetTransactionByIdAsync(id);
    }

    public Task<ReceiptDto?> GetTransactionReceiptAsync(long id, bool reprint = false)
    {
        EnsureCanViewTransactionHistory();
        return _apiService.GetTransactionReceiptAsync(id, reprint);
    }

    public Task<VoidResponseDto?> VoidTransactionAsync(long orderId, string reason, string approverPin)
    {
        EnsureCanViewTransactionHistory();
        return _apiService.VoidTransactionAsync(orderId, reason, approverPin);
    }

    public Task<ReturnResponseDto?> ProcessReturnAsync(ReturnRequestDto request)
    {
        EnsureCanViewTransactionHistory();
        return _apiService.ProcessReturnAsync(request);
    }

    public Task<List<ReturnResponseDto>> GetReturnsAsync(int? branchId = null, int? terminalId = null, int? outletId = null)
    {
        EnsureCanViewTransactionHistory();
        return _apiService.GetReturnsAsync(branchId, terminalId, outletId);
    }

    public Task<TransactionSummaryDto> GetTransactionSummaryAsync(TransactionFilterDto filter)
    {
        EnsureCanViewTransactionHistory();
        return _apiService.GetTransactionSummaryAsync(filter);
    }

    public async Task<List<OrderResponseDto>> GetLocalTransactionsAsync(TransactionFilterDto filter)
    {
        EnsureCanViewTransactionHistory();

        if (_session.Terminal == null)
        {
            return new List<OrderResponseDto>();
        }

        using var db = new LocalPosDbContext();
        db.InitializeDatabase();

        var startLocal = filter.DateFrom ?? BusinessDayClock.GetCurrentBusinessDayStartLocal();
        var targetTerminalId = _session.Terminal.TerminalId;

        var query = db.Orders.AsNoTracking()
            .Where(o => o.TerminalId == targetTerminalId && o.CreatedAt >= startLocal);

        if (!string.IsNullOrWhiteSpace(filter.InvoiceNumber))
        {
            var term = filter.InvoiceNumber.Trim();
            query = query.Where(o =>
                o.InvoiceNumber.Contains(term) ||
                (o.OrderNumber != null && o.OrderNumber.Contains(term)));
        }

        if (!string.IsNullOrWhiteSpace(filter.PaymentStatus))
        {
            query = query.Where(o => o.PaymentStatus == filter.PaymentStatus);
        }

        var localOrders = await query
            .OrderByDescending(o => o.CreatedAt)
            .Take(100)
            .ToListAsync();

        var orderGuids = localOrders.Select(o => o.OrderId).ToList();
        var localItems = await db.OrderItems.AsNoTracking()
            .Where(i => orderGuids.Contains(i.OrderId))
            .OrderBy(i => i.OrderItemId)
            .ToListAsync();
        var localPayments = await db.Payments.AsNoTracking()
            .Where(p => orderGuids.Contains(p.OrderId))
            .ToListAsync();
        var productMetadata = await OfflineCheckoutService.LoadLocalProductMetadataAsync(
            db,
            localItems.Select(i => i.ProductId));

        var mappedList = new List<OrderResponseDto>();
        long syntheticId = -1;
        foreach (var lo in localOrders)
        {
            mappedList.Add(new OrderResponseDto
            {
                OrderId = syntheticId--,
                OrderNumber = !string.IsNullOrWhiteSpace(lo.OrderNumber) ? lo.OrderNumber : lo.InvoiceNumber,
                InvoiceNumber = lo.InvoiceNumber,
                BranchId = lo.BranchId,
                TerminalId = lo.TerminalId,
                CashierId = lo.CashierId,
                OrderType = lo.OrderType,
                TableNumber = lo.TableNumber,
                HeadCount = lo.HeadCount,
                OrderStatus = lo.OrderStatus,
                PaymentStatus = lo.PaymentStatus,
                Subtotal = lo.Subtotal,
                DiscountAmount = lo.DiscountAmount,
                DiscountCode = lo.DiscountCode,
                CustomerName = lo.CustomerName,
                CustomerAddress = lo.CustomerAddress,
                CustomerTin = lo.CustomerTin,
                CustomerBusinessStyle = lo.CustomerBusinessStyle,
                TotalAmount = lo.TotalAmount,
                VatableSales = lo.VatableSales,
                VatExemptSales = lo.VatExemptSales,
                ZeroRatedSales = lo.ZeroRatedSales,
                VatAmount = lo.VatAmount,
                CreatedAt = BusinessDayClock.NormalizeLocal(lo.CreatedAt),
                TransactionTimestamp = new DateTimeOffset(
                    BusinessDayClock.NormalizeLocal(lo.CreatedAt),
                    TimeSpan.FromMinutes(lo.DeviceUtcOffsetMinutes ?? BusinessDayClock.GetDeviceUtcOffsetMinutes())),
                DeviceTimeZoneId = lo.DeviceTimeZoneId,
                DeviceUtcOffsetMinutes = lo.DeviceUtcOffsetMinutes,
                ServerReceivedAtUtc = lo.ServerReceivedAtUtc.HasValue
                    ? BusinessDayClock.NormalizeUtc(lo.ServerReceivedAtUtc.Value)
                    : null,
                Items = OfflineCheckoutService.MapLocalOrderItems(
                    localItems.Where(i => i.OrderId == lo.OrderId),
                    productMetadata),
                Payments = localPayments.Where(p => p.OrderId == lo.OrderId).Select(p => new PaymentResponseDto
                {
                    PaymentId = p.PaymentId,
                    PaymentMethod = p.PaymentMethod,
                    Amount = p.Amount,
                    AmountTendered = p.TenderedAmount,
                    ChangeAmount = p.ChangeAmount,
                    PaymentStatus = p.PaymentStatus,
                    PaidAt = BusinessDayClock.NormalizeLocal(p.PaidAt)
                }).ToList()
            });
        }

        return mappedList;
    }

    public async Task ApplyLocalVoidOverlayAsync(List<OrderResponseDto> transactions)
    {
        EnsureCanViewTransactionHistory();
        if (_session.Terminal == null || transactions == null || transactions.Count == 0)
            return;

        using var db = new LocalPosDbContext();
        db.InitializeDatabase();

        int terminalId = _session.Terminal.TerminalId;
        var pendingVoids = await db.TransactionVoids.AsNoTracking()
            .Where(v => v.TerminalId == terminalId && v.SyncStatus != "SYNCED")
            .ToListAsync();

        if (pendingVoids.Count == 0)
            return;

        var voidedServerIds = new HashSet<long>(
            pendingVoids.Where(v => v.ServerOrderId.HasValue && v.ServerOrderId.Value > 0).Select(v => v.ServerOrderId!.Value));
        var voidedInvoices = new HashSet<string>(
            pendingVoids.Where(v => !string.IsNullOrWhiteSpace(v.OriginalInvoiceNumber)).Select(v => v.OriginalInvoiceNumber!.Trim()),
            StringComparer.OrdinalIgnoreCase);

        foreach (var tx in transactions)
        {
            bool matched = (tx.OrderId > 0 && voidedServerIds.Contains(tx.OrderId))
                || (!string.IsNullOrWhiteSpace(tx.InvoiceNumber) && voidedInvoices.Contains(tx.InvoiceNumber.Trim()));
            if (matched)
            {
                tx.OrderStatus = "VOIDED";
                tx.PaymentStatus = "VOIDED";
            }
        }
    }
}

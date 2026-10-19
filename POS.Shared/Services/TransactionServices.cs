using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using POS.Shared.Interfaces;
using POS.Shared.Models;

namespace POS.Shared.Services;

public class TransactionService(HttpClient http) : ITransactionService
{
    private void AttachToken(HttpClient client)
    {
        // If the shared HttpClient does not have an Authorization header, check if we can grab it or let requests pass.
    }
    public async Task<List<OrderResponseDto>> GetTransactionsAsync(TransactionFilterDto filter)
    {
        var url = "api/transactions?";
        if (filter.DateFrom.HasValue) url += $"dateFrom={Uri.EscapeDataString(filter.DateFrom.Value.ToString("o"))}&";
        if (filter.DateTo.HasValue) url += $"dateTo={Uri.EscapeDataString(filter.DateTo.Value.ToString("o"))}&";
        if (filter.BranchId.HasValue) url += $"branchId={filter.BranchId}&";
        if (filter.OutletId.HasValue) url += $"outletId={filter.OutletId}&";
        if (!string.IsNullOrWhiteSpace(filter.CashierId)) url += $"cashierId={Uri.EscapeDataString(filter.CashierId)}&";
        if (!string.IsNullOrWhiteSpace(filter.OrderNumber)) url += $"orderNumber={Uri.EscapeDataString(filter.OrderNumber)}&";
        if (!string.IsNullOrWhiteSpace(filter.InvoiceNumber)) url += $"invoiceNumber={Uri.EscapeDataString(filter.InvoiceNumber)}&";
        if (!string.IsNullOrWhiteSpace(filter.PaymentMethod)) url += $"paymentMethod={Uri.EscapeDataString(filter.PaymentMethod)}&";
        if (!string.IsNullOrWhiteSpace(filter.OrderStatus)) url += $"orderStatus={Uri.EscapeDataString(filter.OrderStatus)}&";
        if (!string.IsNullOrWhiteSpace(filter.PaymentStatus)) url += $"paymentStatus={Uri.EscapeDataString(filter.PaymentStatus)}&";
        if (!string.IsNullOrWhiteSpace(filter.DiscountCode)) url += $"discountCode={Uri.EscapeDataString(filter.DiscountCode)}&";
        url += $"page={filter.Page}&pageSize={filter.PageSize}";

        return await http.GetFromJsonAsync<List<OrderResponseDto>>(url) ?? new();
    }

    public async Task<OrderResponseDto?> GetTransactionByIdAsync(long id)
    {
        return await http.GetFromJsonAsync<OrderResponseDto>($"api/transactions/{id}");
    }

    public async Task<ReceiptDto?> GetTransactionReceiptAsync(long id, bool reprint = false)
    {
        return await http.GetFromJsonAsync<ReceiptDto>($"api/transactions/{id}/receipt?reprint={reprint}");
    }

    public async Task<VoidResponseDto?> VoidTransactionAsync(long orderId, string reason, string approverPin)
    {
        var response = await http.PostAsJsonAsync($"api/transactions/{orderId}/void", new VoidRequestDto
        {
            OriginalOrderId = orderId,
            VoidReason = reason,
            ApprovedByPinOrUsername = approverPin
        });
        if (!response.IsSuccessStatusCode) return null;
        return await response.Content.ReadFromJsonAsync<VoidResponseDto>();
    }

    public async Task<ReturnResponseDto?> ProcessReturnAsync(ReturnRequestDto request)
    {
        var response = await http.PostAsJsonAsync("api/returns", request);
        if (!response.IsSuccessStatusCode) return null;
        return await response.Content.ReadFromJsonAsync<ReturnResponseDto>();
    }

    public async Task<List<ReturnResponseDto>> GetReturnsAsync(int? branchId = null, int? outletId = null)
    {
        var url = "api/returns?";
        if (branchId.HasValue) url += $"branchId={branchId.Value}&";
        if (outletId.HasValue) url += $"outletId={outletId.Value}&";
        return await http.GetFromJsonAsync<List<ReturnResponseDto>>(url) ?? new();
    }

    public async Task<TransactionSummaryDto> GetTransactionSummaryAsync(TransactionFilterDto filter)
    {
        var url = "api/transactions/summary?";
        if (filter.DateFrom.HasValue) url += $"dateFrom={Uri.EscapeDataString(filter.DateFrom.Value.ToString("o"))}&";
        if (filter.DateTo.HasValue) url += $"dateTo={Uri.EscapeDataString(filter.DateTo.Value.ToString("o"))}&";
        if (filter.BranchId.HasValue) url += $"branchId={filter.BranchId}&";
        if (filter.OutletId.HasValue) url += $"outletId={filter.OutletId}&";
        if (!string.IsNullOrWhiteSpace(filter.CashierId)) url += $"cashierId={Uri.EscapeDataString(filter.CashierId)}&";

        return await http.GetFromJsonAsync<TransactionSummaryDto>(url) ?? new();
    }
}
public class SalesReportApiService(HttpClient http) : ISalesReportService
{
    public async Task<SalesReportResponseDto> GenerateReportAsync(SalesReportRequestDto request, System.Threading.CancellationToken cancellationToken = default)
    {
        var url = "api/sales-reports?";
        if (request.DateTo.HasValue) url += $"dateTo={Uri.EscapeDataString(request.DateTo.Value.ToString("o"))}&";
        if (request.BranchId.HasValue) url += $"branchId={request.BranchId}&";
        if (request.OutletId.HasValue) url += $"outletId={request.OutletId}&";
        if (!string.IsNullOrWhiteSpace(request.CashierId)) url += $"cashierId={Uri.EscapeDataString(request.CashierId)}&";
        if (!string.IsNullOrWhiteSpace(request.PaymentMethod)) url += $"paymentMethod={Uri.EscapeDataString(request.PaymentMethod)}&";
        if (!string.IsNullOrWhiteSpace(request.Grouping)) url += $"grouping={Uri.EscapeDataString(request.Grouping)}&";

        return await http.GetFromJsonAsync<SalesReportResponseDto>(url, cancellationToken) ?? new();
    }
}

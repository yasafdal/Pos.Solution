using Microsoft.EntityFrameworkCore;
using POS.Api.Models;
using POS.Shared.Interfaces;
using POS.Shared.Models;

namespace POS.Api.Services;

public class ReceiptService(PosDbContext context) : IReceiptService
{
    public async Task<ReceiptDto?> GetReceiptAsync(long orderId, bool isReprint = false)
    {
        var order = await context.Orders.AsNoTracking()
            .Include(o => o.OrderItems).ThenInclude(i => i.OrderItemModifiers)
            .Include(o => o.Payments)
            .SingleOrDefaultAsync(o => o.OrderId == orderId);
        if (order == null) return null;

        var payments = order.Payments.Where(p => p.PaymentStatus == "SUCCESS")
            .OrderBy(p => p.PaidAt).ThenBy(p => p.PaymentId).ToList();
        if (order.PaymentStatus != "PAID" || payments.Count == 0 || payments.Sum(p => p.Amount) < order.TotalAmount)
            throw new InvalidOperationException("A receipt is only available for a fully paid order with successful payments.");
        // Branch/outlet labels are current metadata; monetary and item data are sale snapshots.
        var branch = await context.Branches.AsNoTracking().SingleOrDefaultAsync(b => b.BranchId == order.BranchId);
        var outlet = await context.Terminals.AsNoTracking().Include(t => t.Outlet).SingleOrDefaultAsync(o => o.TerminalId == order.TerminalId);
        var productIds = order.OrderItems.Select(i => i.ProductId).Distinct().ToList();
        var productRouting = await context.Products.AsNoTracking()
            .Where(p => productIds.Contains(p.ProductId))
            .Select(p => new { p.ProductId, p.IsKitchenItem, p.IsBarItem })
            .ToDictionaryAsync(p => p.ProductId);

        return new ReceiptDto
        {
            OrderId = order.OrderId,
            OrderNumber = order.OrderNumber,
            BranchId = order.BranchId,
            BranchName = branch?.Name ?? string.Empty,
            BranchCode = branch?.Code,
            BranchAddress = branch?.Address,
            BranchPhone = branch?.Phone,
            TerminalId = order.TerminalId,
            OutletId = outlet?.OutletId ?? 0, OutletName = outlet?.Outlet.Name ?? string.Empty,
            PTUNumber = outlet?.Ptunumber, MINNumber = outlet?.Minnumber, SerialNumber = outlet?.SerialNumber,
            TerminalName = outlet?.Name ?? string.Empty,
            CashierId = order.CashierId,
            OrderType = order.OrderType,
            TableNumber = order.TableNumber,
            HeadCount = order.HeadCount,
            CreatedAt = order.CreatedAt,
            PaidAt = payments.Max(p => p.PaidAt),
            Subtotal = order.Subtotal,
            VatableSales = order.VatableSales,
            VatExemptSales = order.VatExemptSales,
            ZeroRatedSales = order.ZeroRatedSales,
            VatAmount = order.VatAmount,
            DiscountCode = order.DiscountCode,
            CustomerCardNumber = order.CustomerCardNumber,
            DiscountAmount = order.DiscountAmount,
            TotalAmount = order.TotalAmount,
            InvoiceNumber = order.InvoiceNumber,
            InvoiceIssuedAt = order.InvoiceIssuedAt,
            CustomerName = order.CustomerName,
            CustomerAddress = order.CustomerAddress,
            CustomerTin = order.CustomerTin,
            CustomerBusinessStyle = order.CustomerBusinessStyle,
            IsReprint = isReprint,
            Items = order.OrderItems.OrderBy(i => i.OrderItemId).Select(i => new OrderItemResponseDto
            {
                OrderItemId = i.OrderItemId,
                ProductId = i.ProductId,
                ProductName = i.ProductName,
                ProductShortCode = i.ProductShortCode,
                Quantity = i.Quantity,
                BasePrice = i.BasePrice,
                ModifierTotal = i.ModifierTotal,
                UnitPrice = i.UnitPrice,
                LineTotal = i.LineTotal,
                IsKitchenItem = productRouting.TryGetValue(i.ProductId, out var pr) && pr.IsKitchenItem,
                IsBarItem = productRouting.TryGetValue(i.ProductId, out var pr2) && pr2.IsBarItem,
                Modifiers = i.OrderItemModifiers.OrderBy(m => m.OrderItemModifierId).Select(m => new OrderItemModifierResponseDto
                {
                    OrderItemModifierId = m.OrderItemModifierId,
                    ModifierOptionId = m.ModifierOptionId,
                    ModifierName = m.ModifierName,
                    AdditionalPrice = m.AdditionalPrice
                }).ToList()
            }).ToList(),
            Payments = payments.Select(p => new PaymentResponseDto
            {
                PaymentId = p.PaymentId,
                OrderId = p.OrderId,
                PaymentMethod = p.PaymentMethod,
                Amount = p.Amount,
                AmountTendered = p.AmountTendered,
                ChangeAmount = p.ChangeAmount,
                PaymentStatus = p.PaymentStatus,
                ReferenceNumber = p.ReferenceNumber,
                ProcessedBy = p.ProcessedBy,
                PaidAt = p.PaidAt
            }).ToList()
        };
    }
}

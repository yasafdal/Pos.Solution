using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System;
using System.Linq;
using System.Threading.Tasks;
using POS.Api.Models;
using POS.Shared.Models;

namespace POS.Api.Controllers;

[Route("api/[controller]")]
[ApiController]
[Authorize]
public class OrdersController(PosDbContext context, POS.Shared.Interfaces.ISequenceService sequenceService, POS.Api.Services.IDiscountCalculationService discountCalculator) : ControllerBase
{
    [HttpPost]
    public async Task<ActionResult<OrderResponseDto>> CreateOrder(OrderCreateDto dto)
    {
        if (dto.Items == null || dto.Items.Count == 0)
            return BadRequest("Order must contain at least one item.");

        if (dto.Items.Any(i => i.Quantity <= 0 || i.BasePrice < 0 || (i.Modifiers != null && i.Modifiers.Any(m => m.AdditionalPrice < 0))))
            return BadRequest("Item quantities must be positive and prices cannot be negative.");

        var terminal = await context.Terminals.AsNoTracking().Include(t => t.Outlet).ThenInclude(o => o.Branch)
            .SingleOrDefaultAsync(t => t.TerminalId == dto.TerminalId);
        if (terminal == null || terminal.BranchId != dto.BranchId || !terminal.IsActive || !terminal.Outlet.IsActive || !terminal.Outlet.Branch.IsActive)
            return BadRequest("Invalid or inactive Branch / Outlet / Terminal ownership.");
        if (!User.IsInRole("Admin") && int.TryParse(User.FindFirst("branch_id")?.Value, out int callerBranchId) && dto.BranchId != callerBranchId)
            return Forbid();
        if (User.FindFirst("terminal_id")?.Value != dto.TerminalId.ToString() && !User.IsInRole("Admin") && !User.IsInRole("Manager")) return Forbid();

        var lockState = await POS.Api.Services.TerminalClosingLockGuard.EvaluateTerminalLockAsync(
            context, dto.TerminalId, dto.CashierId);
        if (lockState.IsCashierRestricted || lockState.IsTerminalLocked)
            return Conflict(lockState.CashierRestrictionMessage ?? lockState.LockMessage ?? "POS Terminal is locked for the current business day.");

        var licCheck = await new POS.Api.Services.LicensingService(context).VerifyTerminalCanOperateAsync(dto.TerminalId);
        if (!licCheck.Allowed)
            return Conflict(licCheck.ErrorMessage ?? "Terminal license is not valid for processing new sales.");

        using var transaction = await context.Database.BeginTransactionAsync();
        try
        {
            var orderNumber = await sequenceService.GenerateOrderNumberAsync(dto.TerminalId);

            var branch = await context.Branches.AsNoTracking().SingleOrDefaultAsync(b => b.BranchId == dto.BranchId);
            var branchTaxType = branch?.TaxType ?? "VAT-Registered";

            var calcReq = new OrderCalculationRequestDto
            {
                BranchTaxType = branchTaxType,
                DiscountCode = dto.DiscountCode ?? "NONE",
                Items = dto.Items.Select(i => new CartItemCalculationDto
                {
                    ProductId = i.ProductId,
                    ProductName = i.ProductName ?? "Product",
                    Quantity = i.Quantity,
                    UnitPrice = i.BasePrice,
                    ModifierTotal = i.Modifiers?.Sum(m => m.AdditionalPrice) ?? 0m,
                    TaxName = i.TaxName ?? "VATable",
                    IsEligibleForStatutoryDiscount = true
                }).ToList()
            };

            var calcRes = discountCalculator.CalculateOrder(calcReq);

            var serverReceivedAtUtc = POS.Shared.Services.BusinessDayClock.UtcNow();
            var occurredAtLocal = dto.TransactionTimestamp.HasValue
                ? DateTime.SpecifyKind(dto.TransactionTimestamp.Value.DateTime, DateTimeKind.Unspecified)
                : POS.Shared.Services.BusinessDayClock.DeviceNow();
            int resolvedOffsetMinutes = dto.DeviceUtcOffsetMinutes
                ?? (dto.TransactionTimestamp.HasValue
                    ? (int)Math.Round(dto.TransactionTimestamp.Value.Offset.TotalMinutes)
                    : POS.Shared.Services.BusinessDayClock.GetDeviceUtcOffsetMinutes());
            var clockEval = POS.Shared.Services.BusinessDayClock.EvaluateClockDiscrepancy(
                occurredAtLocal,
                serverReceivedAtUtc,
                dto.TransactionTimestamp?.UtcDateTime,
                null,
                resolvedOffsetMinutes);
            if (clockEval.IsSuspicious && !string.IsNullOrWhiteSpace(clockEval.WarningMessage))
            {
                Console.WriteLine($"[OrdersController] Order={orderNumber} Terminal={dto.TerminalId}: {clockEval.WarningMessage}");
            }

            var order = new Order
            {
                OrderNumber = orderNumber,
                BranchId = dto.BranchId,
                OutletId = terminal.OutletId,
                TerminalId = dto.TerminalId,
                TableNumber = dto.TableNumber,
                HeadCount = dto.HeadCount,
                CashierId = dto.CashierId ?? "system",
                ShiftId = dto.ShiftId,
                OrderType = string.IsNullOrWhiteSpace(dto.OrderType) ? "DINING" : dto.OrderType,
                OrderStatus = "PENDING",
                PaymentStatus = "UNPAID",
                DiscountAmount = calcRes.DiscountAmount,
                DiscountTypeId = dto.DiscountTypeId,
                DiscountCode = dto.DiscountCode,
                CustomerCardNumber = dto.CustomerCardNumber,
                VatableSales = calcRes.VatableSales,
                VatExemptSales = calcRes.VatExemptSales,
                ZeroRatedSales = calcRes.ZeroRatedSales,
                VatAmount = calcRes.VatAmount,
                CustomerName = string.IsNullOrWhiteSpace(dto.CustomerName) ? null : dto.CustomerName.Trim(),
                CustomerAddress = string.IsNullOrWhiteSpace(dto.CustomerAddress) ? null : dto.CustomerAddress.Trim(),
                CustomerTin = string.IsNullOrWhiteSpace(dto.CustomerTin) ? null : dto.CustomerTin.Trim(),
                CustomerBusinessStyle = string.IsNullOrWhiteSpace(dto.CustomerBusinessStyle) ? null : dto.CustomerBusinessStyle.Trim(),
                CreatedAt = occurredAtLocal,
                ServerReceivedAtUtc = serverReceivedAtUtc,
                DeviceTimeZoneId = !string.IsNullOrWhiteSpace(dto.DeviceTimeZoneId) ? dto.DeviceTimeZoneId : POS.Shared.Services.BusinessDayClock.GetDeviceTimeZoneId(),
                DeviceUtcOffsetMinutes = resolvedOffsetMinutes,
                ClockSkewSeconds = clockEval.ClockSkewSeconds != 0 ? clockEval.ClockSkewSeconds : null
            };

            decimal subtotal = 0m;

            foreach (var itemDto in dto.Items)
            {
                decimal modifierTotal = 0m;
                var itemModifiers = new List<OrderItemModifier>();

                if (itemDto.Modifiers != null)
                {
                    foreach (var mod in itemDto.Modifiers)
                    {
                        modifierTotal += mod.AdditionalPrice;
                        itemModifiers.Add(new OrderItemModifier
                        {
                            ModifierOptionId = mod.ModifierOptionId,
                            ProductId = mod.ProductId,
                            ModifierName = mod.ModifierName ?? "Modifier",
                            AdditionalPrice = mod.AdditionalPrice
                        });
                    }
                }

                decimal unitPrice = itemDto.IsComplimentary ? 0m : (itemDto.BasePrice + modifierTotal);
                decimal lineTotal = itemDto.IsComplimentary ? 0m : (itemDto.TargetAmount.HasValue && itemDto.TargetAmount.Value > 0
                    ? itemDto.TargetAmount.Value
                    : Math.Round(unitPrice * itemDto.Quantity, 2, MidpointRounding.AwayFromZero));
                subtotal += lineTotal;

                var calcItem = calcRes.CalculatedItems.FirstOrDefault(ci => ci.ProductId == itemDto.ProductId);

                var orderItem = new OrderItem
                {
                    ProductId = itemDto.ProductId,
                    ProductName = itemDto.ProductName ?? "Product",
                    ProductShortCode = itemDto.ProductShortCode,
                    SpecialInstructions = CartItem.NormalizeSpecialInstructions(itemDto.SpecialInstructions),
                    PricingMethod = itemDto.PricingMethod,
                    IsComplimentary = itemDto.IsComplimentary,
                    ParentProductId = itemDto.ParentProductId,
                    Quantity = itemDto.Quantity,
                    BasePrice = itemDto.IsComplimentary ? 0m : itemDto.BasePrice,
                    ModifierTotal = itemDto.IsComplimentary ? 0m : modifierTotal,
                    UnitPrice = unitPrice,
                    LineTotal = lineTotal,
                    DiscountAmount = calcItem?.DiscountAmount ?? 0m,
                    VatType = calcItem?.VatType ?? "VATABLE",
                    VatAmount = calcItem?.VatAmount ?? 0m,
                    OrderItemModifiers = itemModifiers
                };

                order.OrderItems.Add(orderItem);
            }

            order.Subtotal = subtotal;
            order.TotalAmount = calcRes.TotalAmount;

            context.Orders.Add(order);
            await context.SaveChangesAsync();

            if (calcRes.DiscountAmount > 0)
            {
                context.DiscountAuditLogs.Add(new DiscountAuditLog
                {
                    OrderId = order.OrderId,
                    CashierId = dto.CashierId ?? "system",
                    DiscountCode = dto.DiscountCode ?? "UNKNOWN",
                    Action = "APPLIED",
                    CustomerCardNumber = dto.CustomerCardNumber,
                    DiscountAmount = calcRes.DiscountAmount,
                    Reason = "Applied at checkout via POS Client"
                });
                await context.SaveChangesAsync();
            }
            await transaction.CommitAsync();

            var response = MapToDto(order);
            return Ok(response);
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync();
            Console.WriteLine($"Error creating order: {ex}");
            return StatusCode(500, "Internal server error while creating order.");
        }
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<OrderResponseDto>> GetOrder(long id)
    {
        var order = await context.Orders
            .Include(o => o.OrderItems)
                .ThenInclude(i => i.OrderItemModifiers)
                    .ThenInclude(m => m.Product)
            .FirstOrDefaultAsync(o => o.OrderId == id);

        if (order == null) return NotFound();

        if (!User.IsInRole("Admin") && int.TryParse(User.FindFirst("branch_id")?.Value, out int callerBranchId) && order.BranchId != callerBranchId)
            return Forbid();
        if (!User.IsInRole("Admin") && !User.IsInRole("Manager") && int.TryParse(User.FindFirst("terminal_id")?.Value, out int callerTerminalId) && order.TerminalId != callerTerminalId)
            return Forbid();

        return Ok(MapToDto(order));
    }

    private OrderResponseDto MapToDto(Order order)
    {
        var productIds = order.OrderItems.Select(i => i.ProductId).Distinct().ToList();
        var routing = context.Products.AsNoTracking()
            .Where(p => productIds.Contains(p.ProductId))
            .Select(p => new { p.ProductId, p.IsKitchenItem, p.IsBarItem })
            .ToDictionary(p => p.ProductId);

        return new OrderResponseDto
        {
            OrderId = order.OrderId,
            OrderNumber = order.OrderNumber,
            BranchId = order.BranchId,
            OutletId = order.OutletId,
            TerminalId = order.TerminalId,
            TableNumber = order.TableNumber,
            HeadCount = order.HeadCount,
            CashierId = order.CashierId,
            ShiftId = order.ShiftId,
            OrderType = order.OrderType,
            OrderStatus = order.OrderStatus,
            PaymentStatus = order.PaymentStatus,
            Subtotal = order.Subtotal,
            DiscountAmount = order.DiscountAmount,
            TotalAmount = order.TotalAmount,
            CustomerName = order.CustomerName,
            CustomerAddress = order.CustomerAddress,
            CustomerTin = order.CustomerTin,
            CustomerBusinessStyle = order.CustomerBusinessStyle,
            CreatedAt = POS.Shared.Services.BusinessDayClock.NormalizeLocal(order.CreatedAt),
            ServerReceivedAtUtc = order.ServerReceivedAtUtc.HasValue ? POS.Shared.Services.BusinessDayClock.NormalizeUtc(order.ServerReceivedAtUtc.Value) : null,
            DeviceTimeZoneId = order.DeviceTimeZoneId,
            DeviceUtcOffsetMinutes = order.DeviceUtcOffsetMinutes,
            ClockSkewSeconds = order.ClockSkewSeconds,
            Items = order.OrderItems.Select(i => new OrderItemResponseDto
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
                PricingMethod = i.PricingMethod,
                IsComplimentary = i.IsComplimentary,
                ParentProductId = i.ParentProductId,
                IsKitchenItem = routing.TryGetValue(i.ProductId, out var r) && r.IsKitchenItem,
                IsBarItem = routing.TryGetValue(i.ProductId, out var r2) && r2.IsBarItem,
                SpecialInstructions = i.SpecialInstructions,
                Modifiers = i.OrderItemModifiers.Select(m => new OrderItemModifierResponseDto
                {
                    OrderItemModifierId = m.OrderItemModifierId,
                    ModifierOptionId = m.ModifierOptionId,
                    ProductId = m.ProductId,
                    ProductName = m.Product != null ? m.Product.Name : null,
                    ModifierName = m.ModifierName,
                    AdditionalPrice = m.AdditionalPrice
                }).ToList()
            }).ToList()
        };
    }
}

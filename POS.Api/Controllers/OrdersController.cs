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
public class OrdersController(PosDbContext context, POS.Shared.Interfaces.ISequenceService sequenceService) : ControllerBase
{
    [HttpPost]
    public async Task<ActionResult<OrderResponseDto>> CreateOrder(OrderCreateDto dto)
    {
        if (dto.Items == null || dto.Items.Count == 0)
            return BadRequest("Order must contain at least one item.");

        using var transaction = await context.Database.BeginTransactionAsync();
        try
        {
            var orderNumber = await sequenceService.GenerateOrderNumberAsync(dto.OutletId);

            var order = new Order
            {
                OrderNumber = orderNumber,
                BranchId = dto.BranchId,
                OutletId = dto.OutletId,
                CashierId = dto.CashierId ?? "system",
                ShiftId = dto.ShiftId,
                OrderType = string.IsNullOrWhiteSpace(dto.OrderType) ? "DINING" : dto.OrderType,
                OrderStatus = "PENDING",
                PaymentStatus = "UNPAID",
                DiscountAmount = dto.DiscountAmount,
                CustomerName = string.IsNullOrWhiteSpace(dto.CustomerName) ? null : dto.CustomerName.Trim(),
                CustomerAddress = string.IsNullOrWhiteSpace(dto.CustomerAddress) ? null : dto.CustomerAddress.Trim(),
                CustomerTin = string.IsNullOrWhiteSpace(dto.CustomerTin) ? null : dto.CustomerTin.Trim(),
                CustomerBusinessStyle = string.IsNullOrWhiteSpace(dto.CustomerBusinessStyle) ? null : dto.CustomerBusinessStyle.Trim(),
                CreatedAt = DateTime.UtcNow
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
                            ModifierName = mod.ModifierName ?? "Modifier",
                            AdditionalPrice = mod.AdditionalPrice
                        });
                    }
                }

                decimal unitPrice = itemDto.BasePrice + modifierTotal;
                decimal lineTotal = unitPrice * itemDto.Quantity;
                subtotal += lineTotal;

                var orderItem = new OrderItem
                {
                    ProductId = itemDto.ProductId,
                    ProductName = itemDto.ProductName ?? "Product",
                    ProductShortCode = itemDto.ProductShortCode,
                    Quantity = itemDto.Quantity,
                    BasePrice = itemDto.BasePrice,
                    ModifierTotal = modifierTotal,
                    UnitPrice = unitPrice,
                    LineTotal = lineTotal,
                    OrderItemModifiers = itemModifiers
                };

                order.OrderItems.Add(orderItem);
            }

            order.Subtotal = subtotal;
            order.TotalAmount = Math.Max(0m, subtotal - dto.DiscountAmount);

            context.Orders.Add(order);
            await context.SaveChangesAsync();
            await transaction.CommitAsync();

            var response = MapToDto(order);
            return Ok(response);
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync();
            return StatusCode(500, $"Internal server error while creating order: {ex.Message}");
        }
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<OrderResponseDto>> GetOrder(long id)
    {
        var order = await context.Orders
            .Include(o => o.OrderItems)
                .ThenInclude(i => i.OrderItemModifiers)
            .FirstOrDefaultAsync(o => o.OrderId == id);

        if (order == null) return NotFound();

        return Ok(MapToDto(order));
    }

    private static OrderResponseDto MapToDto(Order order)
    {
        return new OrderResponseDto
        {
            OrderId = order.OrderId,
            OrderNumber = order.OrderNumber,
            BranchId = order.BranchId,
            OutletId = order.OutletId,
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
            CreatedAt = order.CreatedAt,
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
                Modifiers = i.OrderItemModifiers.Select(m => new OrderItemModifierResponseDto
                {
                    OrderItemModifierId = m.OrderItemModifierId,
                    ModifierOptionId = m.ModifierOptionId,
                    ModifierName = m.ModifierName,
                    AdditionalPrice = m.AdditionalPrice
                }).ToList()
            }).ToList()
        };
    }
}

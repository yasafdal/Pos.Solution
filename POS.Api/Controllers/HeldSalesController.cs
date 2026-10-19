using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using POS.Api.Models;
using POS.Shared.Models;

namespace POS.Api.Controllers;

[Route("api/held-sales")]
[ApiController]
[Authorize]
public class HeldSalesController(PosDbContext context) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IEnumerable<HeldSaleDto>>> GetHeldSales(
        [FromQuery] int? branchId,
        [FromQuery] int? outletId,
        CancellationToken cancellationToken = default)
    {
        var query = context.HeldSales.AsNoTracking()
            .Include(h => h.HeldSaleItems).ThenInclude(i => i.HeldSaleItemModifiers)
            .Where(h => h.Status == "HELD")
            .AsQueryable();

        if (branchId.HasValue)
            query = query.Where(h => h.BranchId == branchId.Value);

        if (outletId.HasValue)
            query = query.Where(h => h.OutletId == outletId.Value);

        var heldSales = await query
            .OrderByDescending(h => h.CreatedAt)
            .ToListAsync(cancellationToken);

        var result = heldSales.Select(h => MapToDto(h)).ToList();
        return Ok(result);
    }

    [HttpGet("{id:long}")]
    public async Task<ActionResult<HeldSaleDto>> GetHeldSaleById(long id, CancellationToken cancellationToken = default)
    {
        var heldSale = await context.HeldSales.AsNoTracking()
            .Include(h => h.HeldSaleItems).ThenInclude(i => i.HeldSaleItemModifiers)
            .SingleOrDefaultAsync(h => h.HeldSaleId == id, cancellationToken);

        if (heldSale == null) return NotFound();
        return Ok(MapToDto(heldSale));
    }

    [HttpPost]
    public async Task<ActionResult<HeldSaleDto>> CreateHeldSale([FromBody] HeldSaleCreateDto dto, CancellationToken cancellationToken = default)
    {
        if (dto.Items == null || dto.Items.Count == 0)
            return BadRequest("Cannot hold an empty cart.");

        string holdRef = "HOLD-" + DateTime.UtcNow.ToString("yyMMddHHmmss") + "-" + Random.Shared.Next(100, 999);

        var heldSale = new HeldSale
        {
            BranchId = dto.BranchId,
            OutletId = dto.OutletId,
            CashierId = dto.CashierId,
            HoldReference = holdRef,
            CustomerName = string.IsNullOrWhiteSpace(dto.CustomerName) ? null : dto.CustomerName.Trim(),
            Notes = string.IsNullOrWhiteSpace(dto.Notes) ? null : dto.Notes.Trim(),
            Subtotal = dto.Subtotal,
            DiscountAmount = dto.DiscountAmount,
            DiscountCode = dto.DiscountCode,
            CustomerCardNumber = dto.CustomerCardNumber,
            TotalAmount = dto.TotalAmount,
            Status = "HELD",
            CreatedAt = DateTime.UtcNow
        };

        foreach (var itemDto in dto.Items)
        {
            var item = new HeldSaleItem
            {
                ProductId = itemDto.ProductId,
                ProductName = itemDto.ProductName,
                ProductShortCode = itemDto.ProductShortCode,
                Quantity = itemDto.Quantity,
                BasePrice = itemDto.BasePrice,
                LineTotal = itemDto.LineTotal,
                TaxName = itemDto.TaxName ?? "VATable"
            };

            if (itemDto.Modifiers != null)
            {
                foreach (var mod in itemDto.Modifiers)
                {
                    item.HeldSaleItemModifiers.Add(new HeldSaleItemModifier
                    {
                        ModifierOptionId = mod.ModifierOptionId,
                        ModifierName = mod.ModifierName,
                        AdditionalPrice = mod.AdditionalPrice
                    });
                }
            }

            heldSale.HeldSaleItems.Add(item);
        }

        context.HeldSales.Add(heldSale);
        await context.SaveChangesAsync(cancellationToken);

        context.HeldSaleAuditLogs.Add(new HeldSaleAuditLog
        {
            HeldSaleId = heldSale.HeldSaleId,
            HoldReference = heldSale.HoldReference,
            CashierId = heldSale.CashierId,
            BranchId = heldSale.BranchId,
            OutletId = heldSale.OutletId,
            Action = "HELD",
            Details = "Transaction parked successfully."
        });
        await context.SaveChangesAsync(cancellationToken);

        var created = await context.HeldSales.AsNoTracking()
            .Include(h => h.HeldSaleItems).ThenInclude(i => i.HeldSaleItemModifiers)
            .SingleAsync(h => h.HeldSaleId == heldSale.HeldSaleId, cancellationToken);

        return Ok(MapToDto(created));
    }

    [HttpPost("{id:long}/resume")]
    public async Task<ActionResult<HeldSaleDto>> ResumeHeldSale(long id, [FromQuery] int? outletId, CancellationToken cancellationToken = default)
    {
        using var transaction = await context.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken);
        try
        {
            var heldSale = await context.HeldSales
                .Include(h => h.HeldSaleItems).ThenInclude(i => i.HeldSaleItemModifiers)
                .SingleOrDefaultAsync(h => h.HeldSaleId == id, cancellationToken);

            if (heldSale == null) return NotFound();

            // Enforce exact status and concurrency state check
            if (heldSale.Status != "HELD")
                return Conflict("This held sale has already been resumed, cancelled, or processed by another terminal.");

            // Enforce outlet isolation if requested by terminal
            if (outletId.HasValue && heldSale.OutletId != outletId.Value)
                return Forbid("Terminal outlet is unauthorized to resume this held sale.");

            heldSale.Status = "RESUMED";
            heldSale.ResumedAt = DateTime.UtcNow;
            await context.SaveChangesAsync(cancellationToken);

            context.HeldSaleAuditLogs.Add(new HeldSaleAuditLog
            {
                HeldSaleId = heldSale.HeldSaleId,
                HoldReference = heldSale.HoldReference,
                CashierId = heldSale.CashierId,
                BranchId = heldSale.BranchId,
                OutletId = heldSale.OutletId,
                Action = "RESUMED",
                Details = "Transaction resumed to active cart."
            });
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return Ok(MapToDto(heldSale));
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    [HttpPost("{id:long}/cancel")]
    public async Task<ActionResult> CancelHeldSale(long id, CancellationToken cancellationToken = default)
    {
        var heldSale = await context.HeldSales.FindAsync([id], cancellationToken);
        if (heldSale == null) return NotFound();
        if (heldSale.Status != "HELD") return BadRequest("Only active held sales can be cancelled.");

        heldSale.Status = "CANCELLED";
        await context.SaveChangesAsync(cancellationToken);

        context.HeldSaleAuditLogs.Add(new HeldSaleAuditLog
        {
            HeldSaleId = heldSale.HeldSaleId,
            HoldReference = heldSale.HoldReference,
            CashierId = heldSale.CashierId,
            BranchId = heldSale.BranchId,
            OutletId = heldSale.OutletId,
            Action = "CANCELLED",
            Details = "Transaction cancelled and removed."
        });
        await context.SaveChangesAsync(cancellationToken);

        return NoContent();
    }

    private static HeldSaleDto MapToDto(HeldSale h) => new()
    {
        HeldSaleId = h.HeldSaleId,
        BranchId = h.BranchId,
        OutletId = h.OutletId,
        CashierId = h.CashierId,
        HoldReference = h.HoldReference,
        CustomerName = h.CustomerName,
        Notes = h.Notes,
        Subtotal = h.Subtotal,
        DiscountAmount = h.DiscountAmount,
        DiscountCode = h.DiscountCode,
        CustomerCardNumber = h.CustomerCardNumber,
        TotalAmount = h.TotalAmount,
        Status = h.Status,
        CreatedAt = h.CreatedAt,
        ResumedAt = h.ResumedAt,
        Items = h.HeldSaleItems.Select(i => new HeldSaleItemDto
        {
            HeldSaleItemId = i.HeldSaleItemId,
            ProductId = i.ProductId,
            ProductName = i.ProductName,
            ProductShortCode = i.ProductShortCode,
            Quantity = i.Quantity,
            BasePrice = i.BasePrice,
            LineTotal = i.LineTotal,
            TaxName = i.TaxName,
            Modifiers = i.HeldSaleItemModifiers.Select(m => new HeldSaleModifierDto
            {
                HeldSaleItemModifierId = m.HeldSaleItemModifierId,
                ModifierOptionId = m.ModifierOptionId,
                ModifierName = m.ModifierName,
                AdditionalPrice = m.AdditionalPrice
            }).ToList()
        }).ToList()
    };
}

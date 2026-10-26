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
        [FromQuery] int? terminalId,
        CancellationToken cancellationToken = default,
        [FromQuery] int? outletId = null)
    {
        if (!User.IsInRole("Admin") && int.TryParse(User.FindFirst("branch_id")?.Value, out int claimBranchId))
        {
            if (branchId.HasValue && branchId.Value != claimBranchId)
                return Forbid();
            branchId = claimBranchId;
        }

        var query = context.HeldSales.AsNoTracking()
            .Include(h => h.HeldSaleItems).ThenInclude(i => i.HeldSaleItemModifiers)
            .Where(h => h.Status == "HELD")
            .AsQueryable();

        if (branchId.HasValue)
            query = query.Where(h => h.BranchId == branchId.Value);

        if (outletId.HasValue)
        {
            int outId = outletId.Value;
            query = query.Where(h => context.Terminals.Any(t => t.TerminalId == h.TerminalId && t.OutletId == outId));
        }

        if (terminalId.HasValue)
            query = query.Where(h => h.TerminalId == terminalId.Value);

        var heldSales = await query
            .OrderByDescending(h => h.CreatedAt)
            .ToListAsync(cancellationToken);

        var branchesById = await context.Branches.AsNoTracking().ToDictionaryAsync(b => b.BranchId, cancellationToken);
        var outletsById = await context.Outlets.AsNoTracking().ToDictionaryAsync(o => o.OutletId, cancellationToken);
        var terminalsById = await context.Terminals.AsNoTracking().ToDictionaryAsync(t => t.TerminalId, cancellationToken);

        var result = heldSales.Select(h =>
        {
            var dto = MapToDto(h);
            terminalsById.TryGetValue(h.TerminalId, out var term);
            int outId = term?.OutletId ?? 0;
            int brId = h.BranchId > 0 ? h.BranchId : (term?.BranchId ?? 0);
            dto.BranchId = brId;
            dto.BranchName = branchesById.TryGetValue(brId, out var br)
                ? br.Name
                : (brId > 0 ? $"Branch #{brId}" : "Unknown Branch");
            dto.OutletId = outId;
            dto.OutletName = outletsById.TryGetValue(outId, out var outl)
                ? outl.Name
                : (outId > 0 ? $"Outlet #{outId}" : "Unknown / Legacy Outlet");
            dto.TerminalCode = term?.TerminalCode ?? (h.TerminalId > 0 ? $"LEGACY-{h.TerminalId}" : "LEGACY");
            dto.TerminalName = term?.Name ?? (h.TerminalId > 0 ? $"Unknown / Legacy Terminal (#{h.TerminalId})" : "Unknown / Legacy Terminal");
            return dto;
        }).ToList();
        return Ok(result);
    }

    [HttpGet("{id:long}")]
    public async Task<ActionResult<HeldSaleDto>> GetHeldSaleById(long id, CancellationToken cancellationToken = default)
    {
        var heldSale = await context.HeldSales.AsNoTracking()
            .Include(h => h.HeldSaleItems).ThenInclude(i => i.HeldSaleItemModifiers)
            .SingleOrDefaultAsync(h => h.HeldSaleId == id, cancellationToken);

        if (heldSale == null) return NotFound();
        if (!User.IsInRole("Admin") && int.TryParse(User.FindFirst("branch_id")?.Value, out int claimBranchId) && heldSale.BranchId != claimBranchId)
            return Forbid();
        var dto = MapToDto(heldSale);
        var branch = await context.Branches.AsNoTracking().FirstOrDefaultAsync(b => b.BranchId == heldSale.BranchId, cancellationToken);
        var term = heldSale.TerminalId > 0
            ? await context.Terminals.AsNoTracking().Include(t => t.Outlet).FirstOrDefaultAsync(t => t.TerminalId == heldSale.TerminalId, cancellationToken)
            : null;
        dto.BranchName = branch?.Name ?? (heldSale.BranchId > 0 ? $"Branch #{heldSale.BranchId}" : "Unknown Branch");
        dto.OutletId = term?.OutletId ?? 0;
        dto.OutletName = term?.Outlet?.Name ?? (dto.OutletId > 0 ? $"Outlet #{dto.OutletId}" : "Unknown / Legacy Outlet");
        dto.TerminalCode = term?.TerminalCode ?? (heldSale.TerminalId > 0 ? $"LEGACY-{heldSale.TerminalId}" : "LEGACY");
        dto.TerminalName = term?.Name ?? (heldSale.TerminalId > 0 ? $"Unknown / Legacy Terminal (#{heldSale.TerminalId})" : "Unknown / Legacy Terminal");
        return Ok(dto);
    }

    [HttpPost]
    public async Task<ActionResult<HeldSaleDto>> CreateHeldSale([FromBody] HeldSaleCreateDto dto, CancellationToken cancellationToken = default)
    {
        if (dto.Items == null || dto.Items.Count == 0)
            return BadRequest("Cannot hold an empty cart.");

        var terminal = await context.Terminals.AsNoTracking().Include(t => t.Outlet).ThenInclude(o => o.Branch)
            .SingleOrDefaultAsync(t => t.TerminalId == dto.TerminalId, cancellationToken);
        if (terminal == null || terminal.BranchId != dto.BranchId || !terminal.IsActive || !terminal.Outlet.IsActive || !terminal.Outlet.Branch.IsActive)
            return BadRequest("Invalid or inactive terminal ownership.");
        if (User.FindFirst("terminal_id")?.Value != dto.TerminalId.ToString() && !User.IsInRole("Admin") && !User.IsInRole("Manager")) return Forbid();
        string holdRef = "HOLD-" + DateTime.UtcNow.ToString("yyMMddHHmmss") + "-" + Random.Shared.Next(100, 999);

        var heldSale = new HeldSale
        {
            BranchId = dto.BranchId,
            TerminalId = dto.TerminalId,
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
            TerminalId = heldSale.TerminalId,
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
    public async Task<ActionResult<HeldSaleDto>> ResumeHeldSale(long id, [FromQuery] int? terminalId, CancellationToken cancellationToken = default)
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
            if (terminalId.HasValue && heldSale.TerminalId != terminalId.Value)
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
                TerminalId = heldSale.TerminalId,
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
            TerminalId = heldSale.TerminalId,
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
        TerminalId = h.TerminalId,
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

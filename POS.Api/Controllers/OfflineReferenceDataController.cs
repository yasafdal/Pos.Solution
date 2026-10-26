using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using POS.Api.Models;
using POS.Shared.Models;

namespace POS.Api.Controllers;

[Route("api/offline")]
[ApiController]
[Authorize]
public class OfflineReferenceDataController(PosDbContext context) : ControllerBase
{
    [HttpPost("reference-data")]
    public async Task<ActionResult<OfflineReferenceDataResponseDto>> GetReferenceData([FromQuery] int terminalId)
    {
        var outlet = await context.Terminals
            .Include(o => o.Branch)
                .ThenInclude(b => b.BusinessType)
            .AsNoTracking()
            .SingleOrDefaultAsync(o => o.TerminalId == terminalId);

        if (outlet == null) return NotFound("Terminal not found.");
        var physicalOutlet = await context.Outlets.AsNoTracking().SingleAsync(o => o.OutletId == outlet.OutletId);
        if (!outlet.IsActive || !physicalOutlet.IsActive || !outlet.Branch.IsActive) return Conflict("Terminal, outlet or branch is inactive.");
        if (User.FindFirst("terminal_id")?.Value != terminalId.ToString() && !User.IsInRole("Admin") && !User.IsInRole("Manager")) return Forbid();

        var categories = await context.Categories
        .AsNoTracking()
        .Where(c => !c.IsDeleted && c.IsActive)
        .Select(c => new OfflineCategoryDto
        {
            CategoryId = c.CategoryId,
            Name = c.Name,
            ColorCode = c.ColorHex
        })
        .ToListAsync();

        var products = await context.Products
            .AsNoTracking()
            .Where(p => p.IsActive && !p.IsDeleted && p.ProductType != "RawMaterial")
            .Select(p => new OfflineProductDto
            {
                ProductId = p.ProductId,
                CategoryId = p.CategoryId,
                CategoryName = p.Category != null ? p.Category.Name : string.Empty,
                Name = p.Name,
                ShortCode = p.ShortCode,
                UnitValue = p.UnitValue,
                UnitOfMeasure = p.UnitOfMeasure,
                Barcode = p.Barcode,
                SKU = p.Sku,
                ImageUrl = p.ImageUrl,
                Price = p.Price,
                CostPrice = p.CostPrice ?? 0m,
                TaxId = p.TaxId,
                TaxName = p.Tax != null ? p.Tax.Name : "No Tax",
                ProductType = p.ProductType,
                IsActive = p.IsActive,
                IsKitchenItem = p.IsKitchenItem,
                IsBarItem = p.IsBarItem
            })
            .ToListAsync();

        var modifierGroups = await context.ModifierGroups
            .AsNoTracking()
            .Include(mg => mg.ModifierOptions)
            .Select(mg => new OfflineModifierGroupDto
            {
                ModifierGroupId = mg.ModifierGroupId,
                Name = mg.Name,
                IsRequired = mg.IsRequired,
                MinSelections = mg.MinSelections,
                MaxSelections = mg.MaxSelections,
                DisplayOrder = mg.DisplayOrder,
                IsActive = mg.IsActive,
                Options = mg.ModifierOptions
                .Select(mo => new OfflineModifierOptionDto
                {
                    ModifierOptionId = mo.ModifierOptionId,
                    ModifierGroupId = mo.ModifierGroupId,
                    Name = mo.Name,
                    Price = mo.Price,
                    DisplayOrder = mo.DisplayOrder,
                    IsActive = mo.IsActive
                }).OrderBy(o => o.DisplayOrder).ToList()
            })
            .ToListAsync();

        var productModifierMappings = await context.ProductModifierGroups
            .AsNoTracking()
            .Select(pmg => new OfflineProductModifierMappingDto
            {
                ProductModifierGroupId = pmg.ProductModifierGroupId,
                ProductId = pmg.ProductId,
                ModifierGroupId = pmg.ModifierGroupId,
                DisplayOrder = pmg.DisplayOrder
            })
            .ToListAsync();

        var taxes = await context.Taxes
            .AsNoTracking()
            .Select(t => new OfflineTaxDto
            {
                TaxId = t.TaxId,
                Name = t.Name,
                Rate = t.Rate ?? 0m
            })
            .ToListAsync();

        var discounts = await context.DiscountTypes
            .AsNoTracking()
            .Select(d => new OfflineDiscountDto
            {
                DiscountId = d.DiscountTypeId,
                Code = d.Code,
                Name = d.Name,
                DiscountCategory = d.DiscountCategory,
                DiscountMethod = d.DiscountMethod,
                DiscountValue = d.DiscountValue,
                IsStatutory = d.IsStatutory,
                RequiresIdentification = d.RequiresIdentification
            })
            .ToListAsync();

        // Reserve a distinct offline block under the same row lock used by online invoicing.
        // shortcut: 1000 numbers per refresh, tune allocation size when operational usage is measured.
        using var allocation = await context.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
        var activeSeq = context.Database.IsNpgsql()
            ? await context.InvoiceSequences.FromSqlInterpolated($"SELECT * FROM \"InvoiceSequences\" WHERE \"terminal_id\" = {terminalId} AND \"is_active\" = true FOR UPDATE").SingleOrDefaultAsync()
            : await context.InvoiceSequences.SingleOrDefaultAsync(s => s.TerminalId == terminalId && s.IsActive);
        OfflineInvoiceSequenceDto? invoiceSequenceDto = null;
        if (activeSeq != null)
        {
            var next = Math.Max(activeSeq.StartingNumber, checked(activeSeq.LastIssuedNumber + 1));
            var end = Math.Min(activeSeq.EndingNumber ?? long.MaxValue, checked(next + 999));
            if (next <= end)
            {
                invoiceSequenceDto = new OfflineInvoiceSequenceDto
                {
                    SequenceId = activeSeq.InvoiceSequenceId, TerminalId = terminalId,
                    SeriesCode = activeSeq.SeriesCode, Prefix = activeSeq.Prefix ?? string.Empty,
                    StartingNumber = next, EndingNumber = end, LastIssuedNumber = next - 1,
                    PaddingLength = activeSeq.PaddingLength, IsActive = true
                };
                activeSeq.LastIssuedNumber = end;
                activeSeq.UpdatedAt = DateTime.UtcNow;
                await context.SaveChangesAsync();
            }
        }
        await allocation.CommitAsync();

        var usersWithPins = await context.Users
            .AsNoTracking()
            .Where(u => u.PinCode != null && u.PinCode != "")
            .ToListAsync();

        var cashiers = usersWithPins
            .Select(u => new OfflineCashierDto
            {
                UserId = u.UserId,
                Username = u.Username,
                Role = u.Role,
                PinHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(u.PinCode!.Trim())))
            })
            .ToList();

        var cashMovementCategories = await context.CashMovementCategories
            .AsNoTracking()
            .Where(c => c.IsActive)
            .OrderBy(c => c.MovementType)
            .ThenBy(c => c.Name)
            .Select(c => new CashMovementCategoryDto
            {
                CategoryId = c.CategoryId,
                Name = c.Name,
                Description = c.Description,
                MovementType = c.MovementType,
                RequiresApproval = c.RequiresApproval,
                ApprovalThreshold = c.ApprovalThreshold,
                IsActive = c.IsActive
            })
            .ToListAsync();

        var openShiftEntity = await context.CashDrawerShifts
            .AsNoTracking()
            .Include(s => s.Movements)
            .Where(s => s.TerminalId == terminalId && s.Status == "OPEN")
            .OrderByDescending(s => s.OpenedAt)
            .FirstOrDefaultAsync();

        var response = new OfflineReferenceDataResponseDto
        {
            Version = DateTime.UtcNow.Ticks.ToString(),
            ServerTimestamp = DateTime.UtcNow,
            Terminal = new OfflineTerminalConfigDto
            {
                TerminalId = outlet.TerminalId,
                OutletId = outlet.OutletId, OutletName = physicalOutlet.Name, TerminalCode = outlet.TerminalCode,
                IpAddress = outlet.IpAddress, PTUNumber = outlet.Ptunumber, MINNumber = outlet.Minnumber, SerialNumber = outlet.SerialNumber,
                BranchId = outlet.BranchId,
                BusinessTypeId = outlet.Branch?.BusinessTypeId ?? 1,
                BusinessType = outlet.Branch?.BusinessType?.Type ?? "FNB",
                BranchName = outlet.Branch?.Name ?? string.Empty,
                BranchCode = outlet.Branch?.Code,
                BranchAddress = outlet.Branch?.Address,
                BranchPhone = outlet.Branch?.Phone,
                TerminalName = outlet.Name,
                BranchTaxType = outlet.Branch?.TaxType ?? "VAT-Registered",
                DeviceIdentifier = outlet.DeviceIdentifier ?? string.Empty
            },
            Products = products,
            Categories = categories,
            ModifierGroups = modifierGroups,
            ProductModifierMappings = productModifierMappings,
            Taxes = taxes,
            Discounts = discounts,
            InvoiceSequence = invoiceSequenceDto,
            Cashiers = cashiers,
            CashMovementCategories = cashMovementCategories,
            ActiveShift = openShiftEntity != null ? await CashDrawerController.ReadSnapshotAsync(context, openShiftEntity.ShiftId, requireOpen: true) : null
        };

        return Ok(response);
    }
}

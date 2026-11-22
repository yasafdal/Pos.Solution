using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using POS.Api.Models;
using POS.Api.Services;
using POS.Shared.Models;

namespace POS.Api.Controllers;

[Route("api/[controller]")]
[ApiController]
[Authorize]
public class DiscountsController(PosDbContext context, IDiscountCalculationService calculationService) : ControllerBase
{
    // GET: api/discounts
    [HttpGet]
    public async Task<ActionResult<IEnumerable<DiscountType>>> GetDiscountTypes([FromQuery] bool activeOnly = false)
    {
        var query = context.DiscountTypes.AsNoTracking();
        if (activeOnly)
            query = query.Where(d => d.IsActive);

        return await query
            .OrderBy(d => d.DiscountTypeId)
            .ToListAsync();
    }

    // GET: api/discounts/sync?sinceUtc=...&knownHash=...
    [HttpGet("sync")]
    public async Task<ActionResult<OfflineDiscountSyncResponseDto>> SyncDiscountTypes(
        [FromQuery] DateTime? sinceUtc = null,
        [FromQuery] string? knownHash = null)
    {
        var payload = await BuildDiscountSyncPayloadAsync(context, sinceUtc, knownHash);
        return Ok(payload);
    }

    internal static DateTime NormalizeUtc(DateTime value)
    {
        if (value == default) return DateTime.UtcNow;
        return value.Kind == DateTimeKind.Local
            ? value.ToUniversalTime()
            : DateTime.SpecifyKind(value, DateTimeKind.Utc);
    }

    internal static async Task<OfflineDiscountSyncResponseDto> BuildDiscountSyncPayloadAsync(
        PosDbContext context,
        DateTime? sinceUtc,
        string? knownHash)
    {
        var nowUtc = DateTime.UtcNow;
        var allDtos = await context.DiscountTypes
            .AsNoTracking()
            .OrderBy(d => d.DiscountTypeId)
            .Select(d => new OfflineDiscountDto
            {
                DiscountId = d.DiscountTypeId,
                Code = d.Code,
                Name = d.Name,
                DiscountCategory = d.DiscountCategory,
                DiscountMethod = d.DiscountMethod,
                DiscountValue = d.DiscountValue,
                IsStatutory = d.IsStatutory,
                RequiresIdentification = d.RequiresIdentification,
                RequiresApproval = d.RequiresApproval,
                IsActive = d.IsActive,
                UpdatedAtUtc = d.UpdatedAt != default ? d.UpdatedAt : d.CreatedAt
            })
            .ToListAsync();

        foreach (var d in allDtos)
            d.UpdatedAtUtc = NormalizeUtc(d.UpdatedAtUtc);

        string serverHash = DiscountSyncHasher.ComputeStateHash(allDtos);

        if (sinceUtc.HasValue && !string.IsNullOrWhiteSpace(knownHash)
            && string.Equals(knownHash.Trim(), serverHash, StringComparison.OrdinalIgnoreCase))
        {
            return new OfflineDiscountSyncResponseDto
            {
                IsIncremental = true,
                HasChanges = false,
                ServerTimestampUtc = nowUtc,
                StateHash = serverHash,
                Discounts = new List<OfflineDiscountDto>()
            };
        }

        if (sinceUtc.HasValue)
        {
            // Strict greater-than cursor downloads only modified rows; StateHash self-heal guarantees zero missed updates.
            var cursorUtc = NormalizeUtc(sinceUtc.Value);
            var delta = allDtos.Where(d => d.UpdatedAtUtc > cursorUtc).ToList();
            if (delta.Count > 0)
            {
                return new OfflineDiscountSyncResponseDto
                {
                    IsIncremental = true,
                    HasChanges = true,
                    ServerTimestampUtc = nowUtc,
                    StateHash = serverHash,
                    Discounts = delta
                };
            }
        }

        return new OfflineDiscountSyncResponseDto
        {
            IsIncremental = false,
            HasChanges = true,
            ServerTimestampUtc = nowUtc,
            StateHash = serverHash,
            Discounts = allDtos
        };
    }

    internal static async Task<(DiscountType? ActiveDiscount, string? ErrorMessage)> ValidateDiscountActivationAsync(
        PosDbContext context,
        int? discountTypeId,
        string? discountCode)
    {
        if (discountTypeId.HasValue)
        {
            var byId = await context.DiscountTypes.AsNoTracking()
                .FirstOrDefaultAsync(d => d.DiscountTypeId == discountTypeId.Value);
            if (byId == null)
                return (null, "The selected discount type does not exist.");
            if (!byId.IsActive)
                return (null, $"Discount '{byId.Name}' ({byId.Code}) is inactive and cannot be applied.");
            return (byId, null);
        }

        var normalizedCode = (discountCode ?? "NONE").Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(normalizedCode) || normalizedCode == "NONE")
            return (null, null);

        var byCode = await context.DiscountTypes.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Code.ToUpper() == normalizedCode);
        if (byCode != null)
        {
            if (!byCode.IsActive)
                return (null, $"Discount '{byCode.Name}' ({byCode.Code}) is inactive and cannot be applied.");
            return (byCode, null);
        }

        return (null, null);
    }

    // GET: api/discounts/get-discount/5
    [HttpGet("get-discount/{id}")]
    public async Task<ActionResult<DiscountType>> GetDiscount(int id)
    {
        var discount = await context.DiscountTypes.FindAsync(id);
        if (discount == null) return NotFound();
        return Ok(discount);
    }

    // POST: api/discounts/create-discount
    [HttpPost("create-discount")]
    [Authorize(Roles = "Admin,Manager")]
    public async Task<ActionResult<DiscountType>> CreateDiscount(DiscountType dto)
    {
        if (dto == null || string.IsNullOrWhiteSpace(dto.Code) || string.IsNullOrWhiteSpace(dto.Name))
            return BadRequest("Discount Code and Name are required.");

        var validationError = POS.Shared.Services.LocalDiscountCalculationService.ValidateDiscountTypeDefinition(
            dto.Code, dto.DiscountMethod, dto.DiscountValue);
        if (validationError != null)
            return BadRequest(validationError);

        dto.CreatedAt = DateTime.UtcNow;
        dto.UpdatedAt = dto.CreatedAt;

        context.DiscountTypes.Add(dto);
        await context.SaveChangesAsync();
        return CreatedAtAction(nameof(GetDiscount), new { id = dto.DiscountTypeId }, dto);
    }

    // PUT: api/discounts/update-discount/5
    [HttpPut("update-discount/{id}")]
    [Authorize(Roles = "Admin,Manager")]
    public async Task<IActionResult> UpdateDiscount(int id, DiscountType dto)
    {
        if (id != dto.DiscountTypeId) return BadRequest("ID mismatch");
        var discount = await context.DiscountTypes.FindAsync(id);
        if (discount == null) return NotFound();

        var validationError = POS.Shared.Services.LocalDiscountCalculationService.ValidateDiscountTypeDefinition(
            dto.Code ?? discount.Code, dto.DiscountMethod, dto.DiscountValue);
        if (validationError != null)
            return BadRequest(validationError);

        discount.Name = dto.Name;
        discount.DiscountCategory = dto.DiscountCategory;
        discount.DiscountMethod = dto.DiscountMethod;
        discount.DiscountValue = dto.DiscountValue;
        discount.RequiresIdentification = dto.RequiresIdentification;
        discount.IsActive = dto.IsActive;
        discount.UpdatedAt = DateTime.UtcNow;

        await context.SaveChangesAsync();
        return NoContent();
    }

    // DELETE: api/discounts/remove-discount/5
    [HttpDelete("remove-discount/{id}")]
    [Authorize(Roles = "Admin,Manager")]
    public async Task<IActionResult> DeleteDiscount(int id)
    {
        var discount = await context.DiscountTypes.FindAsync(id);
        if (discount == null) return NotFound();
        if (discount.IsStatutory) return BadRequest("Statutory discounts cannot be deleted.");

        context.DiscountTypes.Remove(discount);
        await context.SaveChangesAsync();
        return NoContent();
    }

    // POST: api/discounts/calculate
    [HttpPost("calculate")]
    public async Task<ActionResult<OrderCalculationResultDto>> CalculateDiscount(OrderCalculationRequestDto request)
    {
        if (request == null || request.Items == null || request.Items.Count == 0)
            return BadRequest("Calculation request cannot be empty.");

        try
        {
            var code = (request.DiscountCode ?? "NONE").Trim().ToUpperInvariant();
            var (configured, activationError) = await ValidateDiscountActivationAsync(context, null, code);
            if (activationError != null)
                return BadRequest(activationError);

            if (configured != null && code != "MANUAL"
                && !POS.Shared.Services.LocalDiscountCalculationService.IsStatutoryVatExemptCode(code)
                && string.IsNullOrWhiteSpace(request.DiscountMethod) && !request.DiscountValue.HasValue)
            {
                request.DiscountMethod = configured.DiscountMethod;
                request.DiscountValue = configured.DiscountValue;
            }

            var result = calculationService.CalculateOrder(request);
            return Ok(result);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (Exception ex)
        {
            return BadRequest($"Calculation error: {ex.Message}");
        }
    }

    // GET: api/discounts/audit
    [HttpGet("audit")]
    public async Task<ActionResult<IEnumerable<DiscountAuditLog>>> GetAuditLogs([FromQuery] long? orderId)
    {
        var query = context.DiscountAuditLogs.AsQueryable();
        if (orderId.HasValue)
            query = query.Where(a => a.OrderId == orderId.Value);

        return await query
            .OrderByDescending(a => a.CreatedAt)
            .Take(100)
            .ToListAsync();
    }
}

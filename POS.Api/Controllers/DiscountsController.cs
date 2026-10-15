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
public class DiscountsController(PosDbContext context, IDiscountCalculationService calculationService) : ControllerBase
{
    // GET: api/discounts
    [HttpGet]
    public async Task<ActionResult<IEnumerable<DiscountType>>> GetDiscountTypes()
    {
        return await context.DiscountTypes
            .OrderBy(d => d.DiscountTypeId)
            .ToListAsync();
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
    public async Task<ActionResult<DiscountType>> CreateDiscount(DiscountType dto)
    {
        context.DiscountTypes.Add(dto);
        await context.SaveChangesAsync();
        return CreatedAtAction(nameof(GetDiscount), new { id = dto.DiscountTypeId }, dto);
    }

    // PUT: api/discounts/update-discount/5
    [HttpPut("update-discount/{id}")]
    public async Task<IActionResult> UpdateDiscount(int id, DiscountType dto)
    {
        if (id != dto.DiscountTypeId) return BadRequest("ID mismatch");
        var discount = await context.DiscountTypes.FindAsync(id);
        if (discount == null) return NotFound();

        discount.Name = dto.Name;
        discount.DiscountCategory = dto.DiscountCategory;
        discount.DiscountMethod = dto.DiscountMethod;
        discount.DiscountValue = dto.DiscountValue;
        discount.RequiresIdentification = dto.RequiresIdentification;
        discount.IsActive = dto.IsActive;

        await context.SaveChangesAsync();
        return NoContent();
    }

    // DELETE: api/discounts/remove-discount/5
    [HttpDelete("remove-discount/{id}")]
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
    public ActionResult<OrderCalculationResultDto> CalculateDiscount(OrderCalculationRequestDto request)
    {
        if (request == null || request.Items == null || request.Items.Count == 0)
            return BadRequest("Calculation request cannot be empty.");

        try
        {
            var result = calculationService.CalculateOrder(request);
            return Ok(result);
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

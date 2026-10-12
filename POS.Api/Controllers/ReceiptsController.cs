using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using POS.Api.Models;
using POS.Shared.Interfaces;
using POS.Shared.Models;

namespace POS.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/orders")]
public class ReceiptsController(PosDbContext context, IReceiptService receipts) : ControllerBase
{
    [HttpGet("{id:long}/receipt")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<ActionResult<ReceiptDto>> GetReceipt(long id, [FromQuery] bool reprint = false)
    {
        var owner = await context.Orders.AsNoTracking()
            .Where(o => o.OrderId == id).Select(o => new { o.CashierId }).SingleOrDefaultAsync();
        if (owner == null) return NotFound();

        // Existing sale creation stores the JWT username, not the numeric NameIdentifier.
        // Tokens do not carry branch/outlet scope; terminal IP is not authorization.
        if (!User.IsInRole("Admin") &&
            (string.IsNullOrWhiteSpace(User.Identity?.Name) ||
             !string.Equals(owner.CashierId, User.Identity.Name, StringComparison.Ordinal)))
            return Forbid();

        try
        {
            var receipt = await receipts.GetReceiptAsync(id, reprint);
            return receipt == null ? NotFound() : Ok(receipt);
        }
        catch (InvalidOperationException)
        {
            return Conflict("A receipt is only available for a fully paid order with successful payments.");
        }
    }
}

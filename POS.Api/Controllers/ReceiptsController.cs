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
    [Authorize(Policy = POS.Shared.Constants.PosPermissions.TransactionHistoryPolicy)]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<ActionResult<ReceiptDto>> GetReceipt(long id, [FromQuery] bool reprint = false)
    {
        if (!POS.Shared.Constants.PosPermissions.CanViewTransactionHistory(User))
            return Forbid();

        var owner = await context.Orders.AsNoTracking()
            .Where(o => o.OrderId == id).Select(o => new { o.CashierId, o.BranchId, o.TerminalId }).SingleOrDefaultAsync();
        if (owner == null) return NotFound();

        if (!POS.Shared.Constants.PosPermissions.IsAdmin(User) &&
            int.TryParse(User.FindFirst("branch_id")?.Value, out int callerBranchId) &&
            owner.BranchId != callerBranchId)
            return Forbid();
        try
        {
            var receipt = await receipts.GetReceiptAsync(id, reprint);
            if (receipt != null && reprint)
            {
                try
                {
                    var order = await context.Orders.AsNoTracking().SingleOrDefaultAsync(o => o.OrderId == id);
                    if (order != null)
                    {
                        await context.Database.ExecuteSqlRawAsync(
                            "INSERT INTO \"ReceiptReprintLogs\" (\"OrderId\", \"InvoiceNumber\", \"BranchId\", \"TerminalId\", \"CashierId\", \"ReprintReason\", \"ReprintedAt\", \"PrinterName\", \"PrintStatus\") VALUES ({0}, {1}, {2}, {3}, {4}, {5}, {6}, {7}, {8})",
                            [order.OrderId, (object?)order.InvoiceNumber ?? DBNull.Value, order.BranchId, order.TerminalId, order.CashierId, "CONTROLLED_REPRINT", DateTime.UtcNow, "EPSON TM-T88IV Receipt", "SUCCESS"]);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Warning: Failed to persist reprint audit log: " + ex.Message);
                }
            }
            return receipt == null ? NotFound() : Ok(receipt);
        }
        catch (InvalidOperationException)
        {
            return Conflict("A receipt is only available for a fully paid order with successful payments.");
        }
    }
}

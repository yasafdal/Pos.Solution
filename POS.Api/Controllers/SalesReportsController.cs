using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Threading;
using System.Threading.Tasks;
using POS.Shared.Interfaces;
using POS.Shared.Models;

namespace POS.Api.Controllers;

[Route("api/sales-reports")]
[ApiController]
[Authorize(Roles = "Admin,Manager,SeniorCashier")]
public class SalesReportsController(ISalesReportService salesReportService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<SalesReportResponseDto>> GetSalesReport(
        [FromQuery] DateTime? dateFrom,
        [FromQuery] DateTime? dateTo,
        [FromQuery] int? branchId,
        [FromQuery] int? terminalId,
        [FromQuery] string? cashierId,
        [FromQuery] string? paymentMethod,
        [FromQuery] string? grouping = "DAILY",
        CancellationToken cancellationToken = default,
        [FromQuery] int? outletId = null)
    {
        if (!User.IsInRole("Admin") && int.TryParse(User.FindFirst("branch_id")?.Value, out int claimBranchId))
        {
            if (branchId.HasValue && branchId.Value != claimBranchId)
                return Forbid();
            branchId = claimBranchId;
        }

        if (!User.IsInRole("Admin") && !User.IsInRole("Manager") && int.TryParse(User.FindFirst("terminal_id")?.Value, out int claimTerminalId))
        {
            if (terminalId.HasValue && terminalId.Value != claimTerminalId)
                return Forbid();
            terminalId = claimTerminalId;
        }

        var request = new SalesReportRequestDto
        {
            DateFrom = dateFrom,
            DateTo = dateTo,
            BranchId = branchId,
            TerminalId = terminalId,
            OutletId = outletId,
            CashierId = cashierId,
            PaymentMethod = paymentMethod,
            Grouping = grouping
        };

        var report = await salesReportService.GenerateReportAsync(request, cancellationToken);
        return Ok(report);
    }
}

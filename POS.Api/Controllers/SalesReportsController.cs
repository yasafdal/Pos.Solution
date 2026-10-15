using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
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
        [FromQuery] int? outletId,
        [FromQuery] string? cashierId,
        [FromQuery] string? paymentMethod,
        [FromQuery] string? grouping = "DAILY",
        CancellationToken cancellationToken = default)
    {
        var request = new SalesReportRequestDto
        {
            DateFrom = dateFrom,
            DateTo = dateTo,
            BranchId = branchId,
            OutletId = outletId,
            CashierId = cashierId,
            PaymentMethod = paymentMethod,
            Grouping = grouping
        };

        var report = await salesReportService.GenerateReportAsync(request, cancellationToken);
        return Ok(report);
    }
}

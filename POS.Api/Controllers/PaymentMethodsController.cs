using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using POS.Api.Models;
using POS.Shared.Models;

namespace POS.Api.Controllers;

[Route("api/payment-methods")]
[ApiController]
public class PaymentMethodsController(PosDbContext context) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<List<PaymentMethodDto>>> GetPaymentMethods()
    {
        var methods = await context.PaymentMethods
            .Where(pm => pm.IsActive)
            .OrderBy(pm => pm.DisplayOrder)
            .Select(pm => new PaymentMethodDto
            {
                PaymentMethodId = pm.PaymentMethodId,
                Code = pm.Code,
                Name = pm.Name,
                RequiresReference = pm.RequiresReference,
                IsActive = pm.IsActive,
                DisplayOrder = pm.DisplayOrder
            })
            .ToListAsync();

        return Ok(methods);
    }
}

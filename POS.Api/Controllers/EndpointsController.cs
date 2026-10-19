using Microsoft.AspNetCore.Mvc;
using System.Reflection;

namespace POS.Api.Controllers;

[Route("api/endpoints")]
[ApiController]
public class EndpointsController : ControllerBase
{
    [HttpGet]
    public IActionResult GetEndpoints()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var controllers = assembly.GetTypes()
            .Where(type => typeof(ControllerBase).IsAssignableFrom(type) && !type.IsAbstract)
            .Select(ctrl => new
            {
                Controller = ctrl.Name,
                Route = ctrl.GetCustomAttribute<RouteAttribute>()?.Template ?? ctrl.Name.Replace("Controller", ""),
                Actions = ctrl.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                    .Where(m => m.IsPublic && !m.IsSpecialName)
                    .Select(m => new
                    {
                        Method = m.GetCustomAttributes()
                            .FirstOrDefault(a => a.GetType().Name.StartsWith("Http"))?.GetType().Name.Replace("Http", "").ToUpper() ?? "GET",
                        Action = m.Name,
                        Template = m.GetCustomAttributes()
                            .FirstOrDefault(a => a.GetType().Name.StartsWith("Http"))?
                            .GetType().GetProperty("Template")?.GetValue(m.GetCustomAttributes().First(a => a.GetType().Name.StartsWith("Http")))
                    })
                    .ToList()
            })
            .ToList();

        return Ok(controllers);
    }
}

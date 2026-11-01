using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using POS.Api.Controllers;
using POS.Api.Models;
using POS.Shared.Models;

namespace POS.Tests;

public static partial class Program
{
    private static void SetUserContext(ControllerBase controller, string username, string role, int? branchId = null, int? terminalId = null)
    {
        var claims = new List<Claim>
        {
            new Claim(ClaimTypes.Name, username),
            new Claim(ClaimTypes.NameIdentifier, "1"),
            new Claim(ClaimTypes.Role, role)
        };
        if (branchId.HasValue) claims.Add(new Claim("branch_id", branchId.Value.ToString()));
        if (terminalId.HasValue) claims.Add(new Claim("terminal_id", terminalId.Value.ToString()));

        var identity = new ClaimsIdentity(claims, "TestAuth");
        var principal = new ClaimsPrincipal(identity);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };
    }

    private static Task TestSecurity_ControllerAuthorizationAttributes()
    {
        var assembly = typeof(AuthController).Assembly;
        var controllerTypes = assembly.GetTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract)
            .ToList();

        foreach (var type in controllerTypes)
        {
            if (type.Name == "AuthController") continue; // Public login endpoints

            var hasAuth = type.GetCustomAttribute<AuthorizeAttribute>() != null
                || type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                       .All(m => m.GetCustomAttribute<AuthorizeAttribute>() != null || m.GetCustomAttribute<AllowAnonymousAttribute>() != null);

            Assert(hasAuth, $"Controller '{type.Name}' must have [Authorize] attribute for deny-by-default security.");
        }

        // Verify EndpointsController is strictly Admin-only
        var endpointsAuth = typeof(EndpointsController).GetCustomAttribute<AuthorizeAttribute>();
        Assert(endpointsAuth != null && endpointsAuth.Roles == "Admin", "EndpointsController must be strictly restricted to Admin role.");

        return Task.CompletedTask;
    }

    private static async Task TestSecurity_PrivilegeEscalationPrevented()
    {
        var (conn, db, _) = await CreateTestEnvAsync();
        await using var cleanup = conn;

        var controller = new UsersController(db);
        SetUserContext(controller, "manager_user", "Manager", branchId: 1);

        // Manager attempts to create an Admin user
        var createAdminResult = await controller.CreateUser(new CreateUserDto
        {
            Username = "hacked_admin",
            Password = "Password123!",
            Role = "Admin"
        });
        Assert(createAdminResult.Result is ForbidResult, "Manager must be forbidden from creating an Admin account.");

        // Add an existing admin
        var adminUser = new User
        {
            Username = "super_admin",
            PasswordHash = "hash",
            Role = "Admin"
        };
        db.Users.Add(adminUser);
        await db.SaveChangesAsync();

        // Manager attempts to delete an Admin user
        var deleteAdminResult = await controller.DeleteUser(adminUser.UserId);
        Assert(deleteAdminResult is ForbidResult, "Manager must be forbidden from deleting an Admin account.");
    }

    private static async Task TestSecurity_SensitiveDataMasking()
    {
        var (conn, db, _) = await CreateTestEnvAsync();
        await using var cleanup = conn;

        db.Users.Add(new User
        {
            Username = "cashier_alice",
            PasswordHash = "hash",
            Role = "Cashier",
            PinCode = "1234"
        });
        await db.SaveChangesAsync();

        // 1. Non-admin caller
        var cashierController = new UsersController(db);
        SetUserContext(cashierController, "cashier_alice", "Cashier", branchId: 1, terminalId: 1);

        var nonAdminResult = await cashierController.GetUsers();
        var nonAdminList = (List<UserDto>)((OkObjectResult)nonAdminResult.Result!).Value!;
        var userForCashier = nonAdminList.First(u => u.Username == "cashier_alice");
        Assert(userForCashier.PinCode == "****", "Non-admin user must only see masked PIN codes (****).");

        // 2. Admin caller
        var adminController = new UsersController(db);
        SetUserContext(adminController, "admin_user", "Admin");

        var adminResult = await adminController.GetUsers();
        var adminList = (List<UserDto>)((OkObjectResult)adminResult.Result!).Value!;
        var userForAdmin = adminList.First(u => u.Username == "cashier_alice");
        Assert(userForAdmin.PinCode == "1234", "Admin user can see actual PIN code.");
    }

    private static async Task TestSecurity_CartPriceAndQuantityTampering()
    {
        var (conn, db, _) = await CreateTestEnvAsync();
        await using var cleanup = conn;

        var seq = new POS.Api.Services.SequenceService(db);
        var disc = new POS.Api.Services.DiscountCalculationService();
        var controller = new OrdersController(db, seq, disc);
        SetUserContext(controller, "cashier1", "Cashier", branchId: 1, terminalId: 1);

        // Negative quantity tampering
        var res1 = await controller.CreateOrder(new OrderCreateDto
        {
            BranchId = 1,
            TerminalId = 1,
            Items = new List<OrderItemCreateDto>
            {
                new() { ProductId = 1, BasePrice = 100m, Quantity = -5 }
            }
        });
        Assert(res1.Result is BadRequestObjectResult, "Negative item quantities must be rejected.");

        // Negative price tampering
        var res2 = await controller.CreateOrder(new OrderCreateDto
        {
            BranchId = 1,
            TerminalId = 1,
            Items = new List<OrderItemCreateDto>
            {
                new() { ProductId = 1, BasePrice = -50m, Quantity = 1 }
            }
        });
        Assert(res2.Result is BadRequestObjectResult, "Negative item prices must be rejected.");

        // Negative modifier tampering
        var res3 = await controller.CreateOrder(new OrderCreateDto
        {
            BranchId = 1,
            TerminalId = 1,
            Items = new List<OrderItemCreateDto>
            {
                new()
                {
                    ProductId = 1,
                    BasePrice = 100m,
                    Quantity = 1,
                    Modifiers = new List<OrderItemModifierCreateDto>
                    {
                        new() { ModifierOptionId = 1, AdditionalPrice = -200m }
                    }
                }
            }
        });
        Assert(res3.Result is BadRequestObjectResult, "Negative modifier prices must be rejected.");
    }

    private static async Task TestSecurity_BOLA_CrossBranchOrderAndReturnProtection()
    {
        var (conn, db, _) = await CreateTestEnvAsync();
        await using var cleanup = conn;

        // Seed Branch 2 and Terminal 2
        var branch2 = new Branch { BranchId = 2, Name = "Branch 2", TaxType = "VAT-Registered", IsActive = true };
        var outlet2 = new Outlet { OutletId = 2, BranchId = 2, OutletCode = "OUT2", Name = "Outlet 2", IsActive = true };
        var terminal2 = new Terminal { TerminalId = 2, BranchId = 2, OutletId = 2, TerminalCode = "POS-2", Name = "Counter 2", IsActive = true };
        db.Branches.Add(branch2);
        db.Outlets.Add(outlet2);
        db.Terminals.Add(terminal2);
        await db.SaveChangesAsync();

        // Seed order at Branch 2
        var branch2Order = new Order
        {
            OrderNumber = "ORD-B2-001",
            BranchId = 2,
            OutletId = 2,
            TerminalId = 2,
            CashierId = "cashier_b2",
            OrderType = "DINING",
            Subtotal = 500,
            TotalAmount = 500,
            OrderStatus = "COMPLETED",
            PaymentStatus = "PAID",
            CreatedAt = DateTime.UtcNow
        };
        db.Orders.Add(branch2Order);
        await db.SaveChangesAsync();

        // Caller belongs to Branch 1
        var ordersCtrl = new OrdersController(db, new POS.Api.Services.SequenceService(db), new POS.Api.Services.DiscountCalculationService());
        SetUserContext(ordersCtrl, "cashier_b1", "Cashier", branchId: 1, terminalId: 1);

        var getOrderRes = await ordersCtrl.GetOrder(branch2Order.OrderId);
        Assert(getOrderRes.Result is ForbidResult, "Cashier from Branch 1 must be forbidden from viewing Branch 2 order (BOLA/IDOR protection).");

        // Returns controller
        var returnsCtrl = new ReturnsController(db);
        SetUserContext(returnsCtrl, "cashier_b1", "Cashier", branchId: 1, terminalId: 1);

        var voidRes = await returnsCtrl.VoidTransaction(new VoidRequestDto
        {
            OriginalOrderId = branch2Order.OrderId,
            VoidReason = "Fraud attempt",
            ApprovedByPinOrUsername = "1234"
        });
        Assert(voidRes.Result is ForbidResult, "User from Branch 1 must be forbidden from voiding Branch 2 order.");

        var retRes = await returnsCtrl.ProcessReturn(new ReturnRequestDto
        {
            OriginalOrderId = branch2Order.OrderId,
            ReturnReason = "Fraud attempt",
            ApprovedByPinOrUsername = "1234",
            Items = new List<ReturnItemRequestDto> { new() { OriginalOrderItemId = 1, Quantity = 1 } }
        });
        Assert(retRes.Result is ForbidResult, "User from Branch 1 must be forbidden from processing return on Branch 2 order.");
    }

    private static async Task TestSecurity_BOLA_ManagerCrossBranchHierarchyProtection()
    {
        var (conn, db, _) = await CreateTestEnvAsync();
        await using var cleanup = conn;

        var terminalsCtrl = new TerminalsController(db, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        SetUserContext(terminalsCtrl, "manager_b1", "Manager", branchId: 1);

        // Manager of Branch 1 tries to create terminal at Branch 2
        var createTermRes = await terminalsCtrl.CreateTerminal(new TerminalDto
        {
            BranchId = 2,
            OutletId = 2,
            TerminalCode = "B2-T9",
            Name = "Illegal Terminal"
        });
        Assert(createTermRes.Result is ForbidResult, "Manager of Branch 1 must be forbidden from creating terminal for Branch 2.");

        // Outlets controller
        var outletsCtrl = new OutletsController(db);
        SetUserContext(outletsCtrl, "manager_b1", "Manager", branchId: 1);

        var createOutletRes = await outletsCtrl.CreateOutlet(new OutletDto
        {
            BranchId = 2,
            OutletCode = "B2-OUT9",
            Name = "Illegal Outlet"
        });
        Assert(createOutletRes.Result is ForbidResult, "Manager of Branch 1 must be forbidden from creating outlet for Branch 2.");
    }

    private static async Task TestSecurity_FileUploadSizeValidation()
    {
        var (conn, db, _) = await CreateTestEnvAsync();
        await using var cleanup = conn;

        var env = new TestWebHostEnvironment();
        var ctrl = new ProductsController(db, env);
        SetUserContext(ctrl, "manager1", "Manager", branchId: 1);

        // Mock 6MB file stream
        var content = new byte[6 * 1024 * 1024];
        var stream = new MemoryStream(content);
        var formFile = new FormFile(stream, 0, content.Length, "file", "large_image.png");

        var result = await ctrl.UploadImage(formFile);
        Assert(result is BadRequestObjectResult badReq && badReq.Value?.ToString()?.Contains("5MB") == true,
            "Image upload exceeding 5MB limit must be rejected.");
    }

    private class TestWebHostEnvironment : Microsoft.AspNetCore.Hosting.IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = Path.GetTempPath();
        public Microsoft.Extensions.FileProviders.IFileProvider WebRootFileProvider { get; set; } = null!;
        public string ApplicationName { get; set; } = "POS.Api";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public string EnvironmentName { get; set; } = "Development";
    }
}

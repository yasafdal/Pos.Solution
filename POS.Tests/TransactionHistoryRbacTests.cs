using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using POS.Api.Controllers;
using POS.Api.Models;
using POS.Api.Services;
using POS.Client.Data;
using POS.Client.Services;
using POS.Shared.Constants;
using POS.Shared.Models;
using POS.Shared.Services;

namespace POS.Tests;

public partial class Program
{
    private sealed class RbacOfflineFailingHttpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, System.Threading.CancellationToken cancellationToken) =>
            throw new HttpRequestException("Simulated offline network failure.");
    }
    private static ClaimsPrincipal BuildPrincipal(string username, string role, int? branchId = 1, int? terminalId = 1, string? cashierRoleClaim = null)
    {
        var claims = new List<Claim>
        {
            new Claim(ClaimTypes.Name, username),
            new Claim(ClaimTypes.Role, role)
        };
        if (branchId.HasValue) claims.Add(new Claim("branch_id", branchId.Value.ToString()));
        if (terminalId.HasValue) claims.Add(new Claim("terminal_id", terminalId.Value.ToString()));
        if (!string.IsNullOrWhiteSpace(cashierRoleClaim))
        {
            claims.Add(new Claim("cashier_username", username));
            claims.Add(new Claim(PosPermissions.CashierRoleClaimType, cashierRoleClaim));
        }
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));
    }

    private static string CreateSignedTestJwt(string username, string role, int branchId = 1, int terminalId = 1)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes("SuperSecretTestKeyForJwtSigning1234567890!"));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var jwt = new JwtSecurityToken(
            issuer: "POS.Api",
            audience: "POS.Client",
            claims: new[]
            {
                new Claim(ClaimTypes.Name, username),
                new Claim(ClaimTypes.Role, role),
                new Claim("branch_id", branchId.ToString()),
                new Claim("terminal_id", terminalId.ToString())
            },
            expires: DateTime.UtcNow.AddHours(4),
            signingCredentials: creds);
        return new JwtSecurityTokenHandler().WriteToken(jwt);
    }

    private static async Task TestRbac01_ApiMultiEndpointRoleMatrix()
    {
        var (conn, db, _) = await CreateTestEnvAsync();
        await using var _ = conn;

        // Seed Branch 2 + Outlet 2 + Terminal 2 for cross-branch verification
        db.Branches.Add(new Branch { BranchId = 2, Name = "Branch 2", Code = "B2", TaxType = "VAT-Registered", IsActive = true });
        db.Outlets.Add(new Outlet { OutletId = 2, BranchId = 2, OutletCode = "OUT-2", Name = "Outlet 2", IsActive = true });
        db.Terminals.Add(new Terminal { TerminalId = 2, BranchId = 2, OutletId = 2, TerminalCode = "POS-2", Name = "Counter 2", IsActive = true });

        var orderB1 = new Order
        {
            OrderId = 5001,
            OrderNumber = "POS01-005001",
            InvoiceNumber = "INV-005001",
            BranchId = 1,
            OutletId = 1,
            TerminalId = 1,
            CashierId = "cashier1",
            OrderType = "DINING",
            OrderStatus = "COMPLETED",
            PaymentStatus = "PAID",
            Subtotal = 400m,
            TotalAmount = 400m,
            VatableSales = 357.14m,
            VatAmount = 42.86m,
            CreatedAt = BusinessDayClock.DeviceNow()
        };
        var orderB2 = new Order
        {
            OrderId = 5002,
            OrderNumber = "POS02-005002",
            InvoiceNumber = "INV-005002",
            BranchId = 2,
            OutletId = 2,
            TerminalId = 2,
            CashierId = "cashier2",
            OrderType = "DINING",
            OrderStatus = "COMPLETED",
            PaymentStatus = "PAID",
            Subtotal = 600m,
            TotalAmount = 600m,
            VatableSales = 535.71m,
            VatAmount = 64.29m,
            CreatedAt = BusinessDayClock.DeviceNow()
        };
        db.Orders.AddRange(orderB1, orderB2);
        db.Payments.AddRange(
            new Payment { PaymentId = 5001, OrderId = 5001, PaymentMethod = "CASH", Amount = 400m, AmountTendered = 500m, ChangeAmount = 100m, PaymentStatus = "PAID", ProcessedBy = "cashier1", PaidAt = BusinessDayClock.DeviceNow() },
            new Payment { PaymentId = 5002, OrderId = 5002, PaymentMethod = "CASH", Amount = 600m, AmountTendered = 600m, ChangeAmount = 0m, PaymentStatus = "PAID", ProcessedBy = "cashier2", PaidAt = BusinessDayClock.DeviceNow() });
        db.TransactionVoids.Add(new TransactionVoid
        {
            VoidId = 7001,
            OriginalOrderId = 5001,
            OriginalInvoiceNumber = "INV-005001",
            VoidReason = "Audit test",
            RequestedBy = "cashier1",
            ApprovedBy = "manager1",
            RequestedAt = DateTime.UtcNow,
            ApprovedAt = DateTime.UtcNow,
            VoidedAt = DateTime.UtcNow,
            Status = "APPROVED",
            ReversalReference = "VOID-TEST01"
        });
        db.SalesReturns.Add(new SalesReturn
        {
            ReturnId = 8001,
            OriginalOrderId = 5001,
            BranchId = 1,
            TerminalId = 1,
            ReturnReference = "RET-TEST01",
            ReturnReason = "Damaged",
            Status = "COMPLETED",
            RequestedBy = "cashier1",
            ApprovedBy = "manager1",
            TotalRefundAmount = 100m,
            CreatedAt = DateTime.UtcNow,
            CompletedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var receiptSvc = new POS.Api.Services.ReceiptService(db);
        var txCtrl = new TransactionsController(db, receiptSvc);
        var ordersCtrl = new OrdersController(db, new SequenceService(db), new DiscountCalculationService());
        var receiptsCtrl = new ReceiptsController(db, receiptSvc);
        var returnsCtrl = new ReturnsController(db);

        void SetUser(ClaimsPrincipal user)
        {
            var ctx = new ControllerContext { HttpContext = new DefaultHttpContext { User = user } };
            txCtrl.ControllerContext = ctx;
            ordersCtrl.ControllerContext = ctx;
            receiptsCtrl.ControllerContext = ctx;
            returnsCtrl.ControllerContext = ctx;
        }

        // 1. Cashier (direct JWT), Reconnected Cashier (Role=Terminal, cashier_role=Cashier), and bare Terminal (no cashier_role) -> ALL MUST BE FORBIDDEN
        var deniedPrincipals = new[]
        {
            ("Cashier JWT", BuildPrincipal("cashier1", "Cashier", branchId: 1, terminalId: 1)),
            ("Reconnected Cashier Terminal JWT", BuildPrincipal("cashier1", "Terminal", branchId: 1, terminalId: 1, cashierRoleClaim: "Cashier")),
            ("Bare Terminal JWT", BuildPrincipal("T-01", "Terminal", branchId: 1, terminalId: 1))
        };

        foreach (var (label, principal) in deniedPrincipals)
        {
            Assert(!PosPermissions.CanViewTransactionHistory(principal), $"{label} must not satisfy CanViewTransactionHistory.");
            SetUser(principal);

            Assert((await txCtrl.GetTransactions(null, null, 1, 1, null, null, null, null, null, null, null)).Result is ForbidResult,
                $"{label}: GET api/transactions must return 403 Forbid (even for own transactions).");
            Assert((await txCtrl.GetTransactionById(5001)).Result is ForbidResult,
                $"{label}: GET api/transactions/5001 must return 403 Forbid.");
            Assert((await txCtrl.GetTransactionReceipt(5001, reprint: false)).Result is ForbidResult,
                $"{label}: GET api/transactions/5001/receipt must return 403 Forbid.");
            Assert((await txCtrl.GetTransactionVoid(5001)).Result is ForbidResult,
                $"{label}: GET api/transactions/5001/void must return 403 Forbid.");
            Assert((await txCtrl.GetTransactionSummary(null, null, 1, 1, null)).Result is ForbidResult,
                $"{label}: GET api/transactions/summary must return 403 Forbid.");
            Assert((await ordersCtrl.GetOrder(5001)).Result is ForbidResult,
                $"{label}: GET api/orders/5001 must return 403 Forbid.");
            Assert((await receiptsCtrl.GetReceipt(5001, reprint: false)).Result is ForbidResult,
                $"{label}: GET api/orders/5001/receipt must return 403 Forbid.");
            Assert((await returnsCtrl.GetVoids(1, 1)).Result is ForbidResult,
                $"{label}: GET api/returns/voids must return 403 Forbid.");
            Assert((await returnsCtrl.GetReturns(1, 1)).Result is ForbidResult,
                $"{label}: GET api/returns must return 403 Forbid.");
            Assert((await returnsCtrl.GetReturnById(8001)).Result is ForbidResult,
                $"{label}: GET api/returns/8001 must return 403 Forbid.");
        }

        // 2. Admin -> Global access across branches
        SetUser(BuildPrincipal("admin1", "Admin", branchId: null, terminalId: null));
        var adminList = ((OkObjectResult)(await txCtrl.GetTransactions(null, null, null, null, null, null, null, null, null, null, null)).Result!).Value as IEnumerable<OrderResponseDto>;
        Assert(adminList != null && adminList.Count() == 2, $"Admin must view all transactions across branches (got {adminList?.Count()}).");
        Assert((await txCtrl.GetTransactionById(5001)).Result is OkObjectResult, "Admin must view transaction details.");
        Assert((await txCtrl.GetTransactionReceipt(5001, reprint: false)).Result is OkObjectResult, "Admin must view transaction receipt.");
        Assert((await txCtrl.GetTransactionVoid(5001)).Result is OkObjectResult, "Admin must view transaction void.");
        Assert((await txCtrl.GetTransactionSummary(null, null, null, null, null)).Result is OkObjectResult, "Admin must view transaction summary.");
        Assert((await ordersCtrl.GetOrder(5001)).Result is OkObjectResult, "Admin must view order by id.");
        Assert((await receiptsCtrl.GetReceipt(5001, reprint: false)).Result is OkObjectResult, "Admin must view order receipt.");
        Assert((await returnsCtrl.GetReturns(null, null)).Result is OkObjectResult, "Admin must view returns.");
        Assert((await returnsCtrl.GetVoids(null, null)).Result is OkObjectResult, "Admin must view voids.");

        // 3. Manager (Branch 1 scoped) + Reconnected Manager Terminal JWT + Senior Cashier (with space) -> Allowed within Branch 1, Forbidden on Branch 2
        var allowedSupervisors = new[]
        {
            ("Manager JWT", BuildPrincipal("manager1", "Manager", branchId: 1, terminalId: 1)),
            ("Reconnected Manager Terminal JWT", BuildPrincipal("manager1", "Terminal", branchId: 1, terminalId: 1, cashierRoleClaim: "Manager")),
            ("Senior Cashier JWT", BuildPrincipal("senior1", "Senior Cashier", branchId: 1, terminalId: 1))
        };
        foreach (var (label, principal) in allowedSupervisors)
        {
            Assert(PosPermissions.CanViewTransactionHistory(principal), $"{label} must satisfy CanViewTransactionHistory.");
            SetUser(principal);

            var listRes = ((OkObjectResult)(await txCtrl.GetTransactions(null, null, 1, null, null, null, null, null, null, null, null)).Result!).Value as IEnumerable<OrderResponseDto>;
            Assert(listRes != null && listRes.Count() == 1 && listRes.First().OrderId == 5001,
                $"{label} must see Branch 1 transactions.");
            Assert((await txCtrl.GetTransactionById(5001)).Result is OkObjectResult, $"{label} must see Branch 1 order details.");
            Assert((await txCtrl.GetTransactionById(5002)).Result is ForbidResult, $"{label} must be forbidden from Branch 2 order details.");
            Assert((await txCtrl.GetTransactionReceipt(5002, reprint: false)).Result is ForbidResult, $"{label} must be forbidden from Branch 2 receipt.");
            Assert((await ordersCtrl.GetOrder(5002)).Result is ForbidResult, $"{label} must be forbidden from Branch 2 GET order.");
        }
    }

    private static async Task TestRbac02_ClientServiceOfflineSqliteAndCashierWorkflowPreservation()
    {
        var prevDbPath = AppContext.GetData("POS.LocalDatabasePath");
        var tempDir = Path.Combine(Path.GetTempPath(), $"pos_rbac02_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var dbPath = Path.Combine(tempDir, "local_pos.db");

        try
        {
            AppContext.SetData("POS.LocalDatabasePath", dbPath);
            LocalPosDbContext.ResetInitializationState(dbPath);
            ClientLicenseService.StrictEnforcementMode = false;

            var (conn, apiDb, _) = await CreateTestEnvAsync();
            await using var _ = conn;

            apiDb.Categories.Add(new Category { CategoryId = 1, Name = "Meals" });
            apiDb.Products.Add(new Product { ProductId = 1, CategoryId = 1, Name = "Adobo Rice", ProductType = "Standard", Price = 180m, PricingMethod = "Regular", UnitOfMeasure = "plate", IsActive = true });
            apiDb.InvoiceSequences.Add(new InvoiceSequence { TerminalId = 1, SeriesCode = "S1", Prefix = "INV-", StartingNumber = 1, EndingNumber = 9999, LastIssuedNumber = 0, PaddingLength = 6, IsActive = true });
            apiDb.OrderSequences.Add(new OrderSequence { TerminalId = 1, LastIssuedNumber = 0, UpdatedAt = DateTime.UtcNow });
            await apiDb.SaveChangesAsync();

            using (var seedDb = new LocalPosDbContext())
            {
                seedDb.InitializeDatabase();
                seedDb.TerminalConfigs.Add(new LocalTerminalConfig
                {
                    TerminalId = 1, OutletId = 1, BranchId = 1, TerminalCode = "T-01", TerminalName = "POS 1",
                    DeviceUuid = "rbac-dev-01", TerminalIdentifier = "rbac-dev-01", BranchName = "Main", BranchCode = "BR-01", BranchTaxType = "VAT-Registered",
                    IsActivated = true, IsInitialized = true
                });
                seedDb.Cashiers.AddRange(
                    new LocalCashier { UserId = 1, Username = "cashier1", Role = "Cashier", PinHash = "HASH_CASHIER" },
                    new LocalCashier { UserId = 2, Username = "manager1", Role = "Manager", PinHash = "HASH_MANAGER" },
                    new LocalCashier { UserId = 3, Username = "admin1", Role = "Admin", PinHash = "HASH_ADMIN" });
                seedDb.Products.Add(new LocalProduct { ProductId = 1, CategoryId = 1, Name = "Adobo Rice", Price = 180m, TaxName = "VATable", PricingMethod = "Regular", UnitOfMeasure = "plate", IsActive = true });
                seedDb.InvoiceSequences.Add(new LocalInvoiceSequence { SequenceId = 1, TerminalId = 1, SeriesCode = "S1", StartingNumber = 1, EndingNumber = 9999, LastIssuedNumber = 0, PaddingLength = 6, Prefix = "INV-", IsActive = true });
                seedDb.OrderSequences.Add(new LocalOrderSequence { TerminalId = 1, LastIssuedNumber = 0, UpdatedAt = DateTime.UtcNow });
                seedDb.CashDrawerShifts.Add(new LocalCashDrawerShift
                {
                    ShiftId = 1, ClientShiftId = "rbac-shift-1", BranchId = 1, TerminalId = 1, CashierId = "cashier1",
                    Status = "OPEN", OpeningFloat = 1000m, OpeningConfirmed = true, OpenedAt = DateTime.UtcNow.AddHours(-1)
                });
                await seedDb.SaveChangesAsync();
            }

            var terminalDto = new TerminalConfigDto
            {
                TerminalId = 1, OutletId = 1, BranchId = 1, TerminalCode = "T-01", TerminalName = "POS 1", BranchName = "Main", BranchCode = "BR-01"
            };

            // 1. Offline login as Cashier (role loaded from LocalCashiers SQLite cache)
            var session = new PosSessionService();
            using (var loginDb = new LocalPosDbContext())
            {
                loginDb.InitializeDatabase();
                var cachedCashier = await loginDb.Cashiers.AsNoTracking().SingleAsync(c => c.Username == "cashier1");
                session.SetSession("OFFLINE-SESSION-TOKEN", cachedCashier.Username, cachedCashier.Role, terminalDto);
            }
            Assert(!session.CanViewTransactionHistory, "Cashier session must have CanViewTransactionHistory == false.");

            // 2. Cashier MUST still be able to complete an offline New Sale and receive current-sale receipt!
            var checkout = new OfflineCheckoutService(new LocalPosDbContext(), session);
            var saleRes = await checkout.ProcessSaleAsync(new OrderCreateDto
            {
                ShiftId = 1, BranchId = 1, OutletId = 1, TerminalId = 1, CashierId = "cashier1", OrderType = "DINING",
                Items = new() { new OrderItemCreateDto { ProductId = 1, ProductName = "Adobo Rice", Quantity = 2, BasePrice = 180m, TaxName = "VATable" } }
            }, 500m, "CASH", null, "cashier1");
            Assert(saleRes.Success && saleRes.Receipt != null && saleRes.Receipt.TotalAmount == 360m && saleRes.ChangeAmount == 140m,
                $"Cashier must still complete offline sales & generate current-sale receipt (Error={saleRes.ErrorMessage}).");

            // 3. Cashier MUST still be able to Hold and Resume sales offline!
            var offlineConnectivity = new ToggleableConnectivityService();
            offlineConnectivity.SetOnline(false);
            using var offlineHttp = new HttpClient(new RbacOfflineFailingHttpHandler()) { BaseAddress = new Uri("http://localhost:5032/") };
            var heldSvc = new ClientHeldSaleService(offlineHttp, offlineConnectivity, session);
            var held = await heldSvc.CreateHeldSaleAsync(new HeldSaleCreateDto
            {
                BranchId = 1, OutletId = 1, TerminalId = 1, CashierId = "cashier1", CustomerName = "Table 3",
                Subtotal = 180m, TotalAmount = 180m,
                Items = new() { new HeldSaleItemDto { ProductId = 1, ProductName = "Adobo Rice", Quantity = 1, BasePrice = 180m, LineTotal = 180m, TaxName = "VATable" } }
            });
            Assert(held != null && held.HeldSaleId < 0, "Cashier must still create offline Held Sales.");
            var heldList = await heldSvc.GetHeldSalesAsync(1, 1, 1);
            Assert(heldList.Count == 1 && heldList[0].CustomerName == "Table 3", "Cashier must still view & resume offline Held Sales.");

            // 4. Cashier attempting to bypass UI via ClientTransactionService (both local SQLite & API methods) MUST be blocked!
            var clientTxSvc = new ClientTransactionService(offlineHttp, session);
            async Task AssertUnauthorized(Func<Task> action, string opName)
            {
                bool threw = false;
                try { await action(); }
                catch (UnauthorizedAccessException) { threw = true; }
                Assert(threw, $"Cashier must receive UnauthorizedAccessException on ClientTransactionService.{opName}.");
            }

            await AssertUnauthorized(() => clientTxSvc.GetLocalTransactionsAsync(new TransactionFilterDto()), "GetLocalTransactionsAsync");
            await AssertUnauthorized(() => clientTxSvc.GetTransactionsAsync(new TransactionFilterDto()), "GetTransactionsAsync");
            await AssertUnauthorized(() => clientTxSvc.GetTransactionByIdAsync(1), "GetTransactionByIdAsync");
            await AssertUnauthorized(() => clientTxSvc.GetTransactionReceiptAsync(1, false), "GetTransactionReceiptAsync");
            await AssertUnauthorized(() => clientTxSvc.GetTransactionSummaryAsync(new TransactionFilterDto()), "GetTransactionSummaryAsync");
            await AssertUnauthorized(() => clientTxSvc.GetReturnsAsync(1, 1, 1), "GetReturnsAsync");

            // 5. Simulate app restart offline: reload Cashier role from SQLite -> still blocked, while LocalOrders record is intact!
            LocalPosDbContext.ResetInitializationState(dbPath);
            var restartedSession = new PosSessionService();
            using (var restartDb = new LocalPosDbContext())
            {
                restartDb.InitializeDatabase();
                var cachedCashier = await restartDb.Cashiers.AsNoTracking().SingleAsync(c => c.Username == "cashier1");
                restartedSession.SetSession("OFFLINE-SESSION-TOKEN", cachedCashier.Username, cachedCashier.Role, terminalDto);
                Assert(await restartDb.Orders.CountAsync() == 1, "Cashier's persisted LocalOrder must NOT be deleted or modified.");
            }
            var restartedClientTxSvc = new ClientTransactionService(offlineHttp, restartedSession);
            await AssertUnauthorized(() => restartedClientTxSvc.GetLocalTransactionsAsync(new TransactionFilterDto()), "GetLocalTransactionsAsync (after restart)");

            // 6. Background offline synchronization MUST still upload Cashier's offline sale cleanly!
            using var syncHttp = CreateInMemoryOfflineSyncHttpClient(apiDb, "cashier1", "Terminal", 1, 1);
            var syncSvc = new OfflineTransactionSyncService(new LocalPosDbContext());
            var pending = await syncSvc.GetPendingOutboxItemsAsync();
            Assert(pending.Count == 1, "Cashier's offline sale must remain queued in SyncOutboxes.");
            Assert(await syncSvc.UploadTransactionAsync(pending[0], syncHttp), "Offline transaction sync must still succeed for Cashier sales.");
            Assert(await apiDb.Orders.CountAsync(o => o.InvoiceNumber == saleRes.InvoiceNumber) == 1, "Synced Cashier sale must exist on server.");

            // 7. Manager & Admin logging in offline CAN query local SQLite Transaction History and see the Cashier's sale!
            var managerSession = new PosSessionService();
            using (var mgrDb = new LocalPosDbContext())
            {
                var cachedMgr = await mgrDb.Cashiers.AsNoTracking().SingleAsync(c => c.Username == "manager1");
                managerSession.SetSession("OFFLINE-SESSION-TOKEN", cachedMgr.Username, cachedMgr.Role, terminalDto);
            }
            var mgrTxSvc = new ClientTransactionService(offlineHttp, managerSession);
            var mgrLocalList = await mgrTxSvc.GetLocalTransactionsAsync(new TransactionFilterDto());
            Assert(mgrLocalList.Count == 1 && mgrLocalList[0].InvoiceNumber == saleRes.InvoiceNumber && mgrLocalList[0].CashierId == "cashier1",
                "Manager must retain offline local SQLite Transaction History access.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            AppContext.SetData("POS.LocalDatabasePath", prevDbPath);
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private static async Task TestRbac03_AccountSwitchingClearsPriorPermissionsAndToken()
    {
        var prevDbPath = AppContext.GetData("POS.LocalDatabasePath");
        var tempDir = Path.Combine(Path.GetTempPath(), $"pos_rbac03_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var dbPath = Path.Combine(tempDir, "local_pos.db");

        try
        {
            AppContext.SetData("POS.LocalDatabasePath", dbPath);
            LocalPosDbContext.ResetInitializationState(dbPath);

            using (var seedDb = new LocalPosDbContext())
            {
                seedDb.InitializeDatabase();
                seedDb.Orders.Add(new LocalOrder
                {
                    OrderId = Guid.NewGuid().ToString(),
                    OrderNumber = "POS01-000001",
                    InvoiceNumber = "INV-000001",
                    BranchId = 1,
                    OutletId = 1,
                    TerminalId = 1,
                    CashierId = "cashier1",
                    OrderStatus = "COMPLETED",
                    PaymentStatus = "PAID",
                    Subtotal = 100m,
                    TotalAmount = 100m,
                    CreatedAt = BusinessDayClock.DeviceNow()
                });
                await seedDb.SaveChangesAsync();
            }

            var terminal = new TerminalConfigDto { TerminalId = 1, OutletId = 1, BranchId = 1, TerminalCode = "T-01", TerminalName = "POS 1" };
            var session = new PosSessionService();
            int sessionChangeEvents = 0;
            session.OnChange += () => sessionChangeEvents++;

            using var http = new HttpClient(new RbacOfflineFailingHttpHandler()) { BaseAddress = new Uri("http://localhost:5032/") };
            var clientTxSvc = new ClientTransactionService(http, session);

            // Step 1: Log in as Admin
            string adminJwt = CreateSignedTestJwt("admin1", "Admin");
            session.SetSession(adminJwt, "admin1", "Admin", terminal);
            DeviceActivationService.ActiveTerminalToken = adminJwt;
            http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", adminJwt);

            Assert(session.CanViewTransactionHistory && session.HasPermission(PosPermissions.ViewTransactionHistory),
                "Admin must have ViewTransactionHistory permission.");
            var adminLocal = await clientTxSvc.GetLocalTransactionsAsync(new TransactionFilterDto());
            Assert(adminLocal.Count == 1, "Admin must read local transactions.");

            // Step 2: Switch Cashier -> ClearSession() + sign in as Cashier
            session.ClearSession();
            Assert(!session.IsAuthenticated && !session.CanViewTransactionHistory,
                "ClearSession must immediately revoke permissions.");

            // Verify even if ActiveTerminalToken/Http header still held Admin's JWT, HasValidOnlineBearerToken rejects it for cashier1!
            session.SetSession("OFFLINE-SESSION-TOKEN", "cashier1", "Cashier", terminal);
            Assert(!DeviceActivationService.HasValidOnlineBearerToken(http, session),
                "Previous Admin JWT must NOT be accepted as a valid bearer token for switched Cashier account.");

            DeviceActivationService.ActiveTerminalToken = null;
            http.DefaultRequestHeaders.Authorization = null;

            Assert(!session.CanViewTransactionHistory && !session.HasPermission(PosPermissions.ViewTransactionHistory),
                "Switched Cashier must NOT retain Admin's ViewTransactionHistory permission.");
            bool cashierBlocked = false;
            try { await clientTxSvc.GetLocalTransactionsAsync(new TransactionFilterDto()); }
            catch (UnauthorizedAccessException) { cashierBlocked = true; }
            Assert(cashierBlocked, "Switched Cashier must be blocked from local Transaction History.");

            // Step 3: Switch to Manager -> regains authorized access cleanly
            string managerJwt = CreateSignedTestJwt("manager1", "Manager");
            session.SetSession(managerJwt, "manager1", "Manager", terminal);
            Assert(session.CanViewTransactionHistory, "Switched Manager must gain authorized Transaction History permission.");
            var mgrLocal = await clientTxSvc.GetLocalTransactionsAsync(new TransactionFilterDto());
            Assert(mgrLocal.Count == 1, "Switched Manager must read local transactions.");
            Assert(sessionChangeEvents >= 4, "PosSessionService.OnChange must notify UI on every session transition.");
        }
        finally
        {
            DeviceActivationService.ActiveTerminalToken = null;
            SqliteConnection.ClearAllPools();
            AppContext.SetData("POS.LocalDatabasePath", prevDbPath);
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }
}

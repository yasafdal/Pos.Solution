using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using POS.Api.Controllers;
using POS.Api.Models;
using POS.Api.Services;
using POS.Client.Data;
using POS.Client.Services;
using POS.Shared.Helpers;
using POS.Shared.Models;
using POS.Shared.Services;

namespace POS.Tests;

public static partial class Program
{
    private sealed class TempLocalDbScope : IDisposable
    {
        private readonly object? _prevLocalDbPath = AppContext.GetData("POS.LocalDatabasePath");
        private readonly string? _prevShiftEnvPath = Environment.GetEnvironmentVariable("POS_TEST_LOCAL_DB_PATH");
        public string DbPath { get; }

        public TempLocalDbScope(string prefix = "offline", bool setShiftRegressionEnv = false)
        {
            DbPath = Path.Combine(Path.GetTempPath(), $"pos_{prefix}_{Guid.NewGuid():N}.db");
            AppContext.SetData("POS.LocalDatabasePath", DbPath);
            if (setShiftRegressionEnv)
            {
                Environment.SetEnvironmentVariable("POS_TEST_LOCAL_DB_PATH", DbPath);
            }

            ResetOfflineTestStaticState();
        }

        public void Dispose()
        {
            AppContext.SetData("POS.LocalDatabasePath", _prevLocalDbPath);
            Environment.SetEnvironmentVariable("POS_TEST_LOCAL_DB_PATH", _prevShiftEnvPath);
            ResetOfflineTestStaticState();
            SqliteConnection.ClearAllPools();

            foreach (var candidate in new[] { DbPath, DbPath + "-wal", DbPath + "-shm" })
            {
                try
                {
                    if (File.Exists(candidate))
                    {
                        File.Delete(candidate);
                    }
                }
                catch
                {
                    // Best-effort temp cleanup
                }
            }
        }
    }

    private static void ResetOfflineTestStaticState()
    {
        DeviceActivationService.ActiveTerminalToken = null;
        ResetLicensingTestHooks();
    }

    private static void SetDeterministicManilaClock(DateTime utcNow, long tickCount64 = 50_000L)
    {
        var utc = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);
        BusinessDayClock.UtcNowProvider = () => utc;
        BusinessDayClock.TickCount64Provider = () => tickCount64;
        BusinessDayClock.DeviceNowOffsetProvider = () => new DateTimeOffset(DateTime.SpecifyKind(utc.AddHours(8), DateTimeKind.Unspecified), TimeSpan.FromHours(8));
        BusinessDayClock.DeviceTimeZoneProvider = () => BusinessDayClock.ManilaTimeZone;
    }

    private static string Base64UrlEncodeUtf8(string raw)
    {
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(raw))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static string CreateSyntheticJwt(string username = "cashier1", string role = "Cashier", int terminalId = 1, int branchId = 1)
    {
        long exp = DateTimeOffset.UtcNow.AddHours(6).ToUnixTimeSeconds();
        string header = Base64UrlEncodeUtf8("{\"alg\":\"none\",\"typ\":\"JWT\"}");
        string payload = Base64UrlEncodeUtf8(JsonSerializer.Serialize(new
        {
            unique_name = username,
            role,
            terminal_id = terminalId.ToString(),
            branch_id = branchId.ToString(),
            exp
        }));
        return $"{header}.{payload}.sig";
    }

    private static PosSessionService CreateOfflineTestSession(
        string token = "OFFLINE-SESSION-TOKEN",
        string username = "cashier1",
        string role = "Cashier",
        int terminalId = 1,
        int outletId = 1,
        int branchId = 1)
    {
        var session = new PosSessionService();
        session.SetSession(token, username, role, new TerminalConfigDto
        {
            TerminalId = terminalId,
            OutletId = outletId,
            BranchId = branchId,
            TerminalCode = "POS-1",
            TerminalName = "POS1",
            BranchName = "Main Branch",
            BranchCode = "BR-01",
            VatDisplay = true
        });
        return session;
    }

    private static string ComputeUpperSha256Hex(string raw)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw.Trim())));
    }

    private static async Task SeedOfflineBaselineAsync(
        bool configureOpeningFund = true,
        decimal openingFund = 1000m,
        long lastInvoiceNumber = 0,
        int terminalId = 1,
        int outletId = 1,
        int branchId = 1)
    {
        using var db = new LocalPosDbContext();
        db.InitializeDatabase();

        db.Branches.Add(new LocalBranch
        {
            BranchId = branchId,
            Name = "Main Branch",
            Code = "BR-01",
            TaxType = "VAT-Registered",
            VatDisplay = true,
            IsActive = true,
            UpdatedAt = BusinessDayClock.UtcNow()
        });

        db.TerminalConfigs.Add(new LocalTerminalConfig
        {
            TerminalId = terminalId,
            OutletId = outletId,
            OutletName = "Counter 1",
            TerminalCode = "POS-1",
            TerminalName = "POS1",
            TerminalIdentifier = "DEV-UUID-1",
            DeviceUuid = "DEV-UUID-1",
            BranchId = branchId,
            BranchName = "Main Branch",
            BranchCode = "BR-01",
            BranchTaxType = "VAT-Registered",
            VatDisplay = true,
            IsActivated = true,
            IsInitialized = true,
            AllowSync = true,
            DefaultOpeningFund = configureOpeningFund ? openingFund : null,
            EffectiveOpeningFund = configureOpeningFund ? openingFund : null,
            OpeningFundSource = configureOpeningFund ? "OUTLET_DEFAULT" : null,
            LastKnownServerUtc = BusinessDayClock.UtcNow(),
            LastKnownServerTickCount64 = BusinessDayClock.TickCount64()
        });

        db.Products.Add(new LocalProduct
        {
            ProductId = 1,
            CategoryId = 1,
            CategoryName = "Meals",
            Name = "Lechon Meal",
            SKU = "LM-01",
            Price = 224m,
            TaxName = "VATable",
            IsActive = true
        });

        db.InvoiceSequences.Add(new LocalInvoiceSequence
        {
            SequenceId = 1,
            TerminalId = terminalId,
            SeriesCode = "TXN1",
            Prefix = "TXN1-",
            StartingNumber = 1,
            EndingNumber = 10000,
            LastIssuedNumber = lastInvoiceNumber,
            PaddingLength = 7,
            IsActive = true
        });

        db.Cashiers.AddRange(
            new LocalCashier { UserId = 1, Username = "cashier1", Role = "Cashier", PinHash = ComputeUpperSha256Hex("1111") },
            new LocalCashier { UserId = 2, Username = "cashier2", Role = "Cashier", PinHash = ComputeUpperSha256Hex("2222") },
            new LocalCashier { UserId = 3, Username = "manager1", Role = "Manager", PinHash = ComputeUpperSha256Hex("9999") });

        await db.SaveChangesAsync();
    }

    private static OrderCreateDto BuildSingleCashOrderDto(
        long shiftId,
        string? clientShiftId = null,
        string cashierId = "cashier1",
        int branchId = 1,
        int outletId = 1,
        int terminalId = 1)
    {
        return new OrderCreateDto
        {
            BranchId = branchId,
            OutletId = outletId,
            TerminalId = terminalId,
            ShiftId = shiftId,
            ClientShiftId = clientShiftId,
            CashierId = cashierId,
            OrderType = "DINING",
            Items =
            [
                new OrderItemCreateDto
                {
                    ProductId = 1,
                    ProductName = "Lechon Meal",
                    Quantity = 1m,
                    BasePrice = 224m,
                    TaxName = "VATable"
                }
            ]
        };
    }

    private static HttpResponseMessage JsonOk(object value)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(value)
        };
    }

    private static HttpResponseMessage ActionResultToHttpResponse<T>(ActionResult<T> actionResult)
    {
        if (actionResult.Result != null)
        {
            return ObjectResultToHttpResponse(actionResult.Result);
        }

        if (actionResult.Value != null)
        {
            return JsonOk(actionResult.Value);
        }

        return new HttpResponseMessage(HttpStatusCode.OK);
    }

    private static HttpResponseMessage ObjectResultToHttpResponse(IActionResult actionResult)
    {
        if (actionResult is OkObjectResult ok)
        {
            return JsonOk(ok.Value!);
        }

        if (actionResult is NoContentResult)
        {
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }

        if (actionResult is NotFoundObjectResult nf)
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent(nf.Value?.ToString() ?? "Not found")
            };
        }

        if (actionResult is NotFoundResult)
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        if (actionResult is ConflictObjectResult conflict)
        {
            return new HttpResponseMessage(HttpStatusCode.Conflict)
            {
                Content = conflict.Value is string s ? new StringContent(s) : JsonContent.Create(conflict.Value)
            };
        }

        if (actionResult is UnauthorizedObjectResult unauth)
        {
            return new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = unauth.Value is string s ? new StringContent(s) : JsonContent.Create(unauth.Value)
            };
        }

        if (actionResult is ObjectResult obj)
        {
            var code = (HttpStatusCode)(obj.StatusCode ?? 500);
            return new HttpResponseMessage(code)
            {
                Content = obj.Value is string s ? new StringContent(s) : JsonContent.Create(obj.Value)
            };
        }

        if (actionResult is StatusCodeResult sc)
        {
            return new HttpResponseMessage((HttpStatusCode)sc.StatusCode);
        }

        return new HttpResponseMessage(HttpStatusCode.Forbidden);
    }

    private static OfflineSyncController CreateTerminalSyncController(PosDbContext serverDb, int terminalId = 1, int branchId = 1)
    {
        return new OfflineSyncController(serverDb)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                    [
                        new Claim(ClaimTypes.Name, "POS-1"),
                        new Claim(ClaimTypes.Role, "Terminal"),
                        new Claim("terminal_id", terminalId.ToString()),
                        new Claim("branch_id", branchId.ToString())
                    ], "TestTerminalAuth"))
                }
            }
        };
    }

    // OFFLINE-01: Disconnected sale & health check classification
    private static async Task TestOffline01_DisconnectedSaleAndHealthChecks()
    {
        using var scope = new TempLocalDbScope("off01");
        SetDeterministicManilaClock(new DateTime(2026, 11, 14, 6, 0, 0, DateTimeKind.Utc)); // 14:00 Manila

        await SeedOfflineBaselineAsync();
        using (var db = new LocalPosDbContext())
        {
            db.CashDrawerShifts.Add(new LocalCashDrawerShift
            {
                ShiftId = -1,
                ClientShiftId = "shift-offline-01",
                BranchId = 1,
                TerminalId = 1,
                CashierId = "cashier1",
                OpeningFloat = 1000m,
                ExpectedBalance = 1000m,
                OpeningConfirmed = true,
                Status = "OPEN",
                OpenedAt = BusinessDayClock.UtcNow().AddHours(-1)
            });
            await db.SaveChangesAsync();
        }

        // 1. Verify OfflineSyncController.GetSyncHealth returns 503 DB_UNAVAILABLE when relational DB cannot connect
        var brokenDbPath = Path.Combine(Path.GetTempPath(), $"missing_dir_{Guid.NewGuid():N}", "broken.db");
        var brokenOpts = new DbContextOptionsBuilder<PosDbContext>()
            .UseSqlite($"Data Source={brokenDbPath};Mode=ReadOnly")
            .Options;
        await using var brokenDb = new TestPosDbContext(brokenOpts);
        var brokenHealthCtrl = new OfflineSyncController(brokenDb);
        var dbDownAction = await brokenHealthCtrl.GetSyncHealth();
        var dbDownObj = dbDownAction as ObjectResult;
        Assert(dbDownObj != null && dbDownObj.StatusCode == 503,
            $"GetSyncHealth must return 503 when DB is unavailable (actual: {dbDownObj?.StatusCode}).");
        string dbDownJson = JsonSerializer.Serialize(dbDownObj!.Value);
        Assert(dbDownJson.Contains("DB_UNAVAILABLE", StringComparison.Ordinal),
            "503 GetSyncHealth response must specify Status=DB_UNAVAILABLE.");

        // 2. Verify PosConnectivityService probes across unreachable API, timeout, 503 DB-down, HTML captive-portal 200 OK, and healthy API
        var session = CreateOfflineTestSession("OFFLINE-SESSION-TOKEN", "cashier1", "Cashier");

        using var unreachableHttp = new HttpClient(new InProcSyncHandler(_ => throw new HttpRequestException("Host unreachable"))) { BaseAddress = new Uri("http://localhost/") };
        using var timeoutHttp = new HttpClient(new InProcSyncHandler(_ => throw new TaskCanceledException("Simulated timeout"))) { BaseAddress = new Uri("http://localhost/") };
        using var dbDownHttp = new HttpClient(new InProcSyncHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent(dbDownJson, Encoding.UTF8, "application/json")
            }))) { BaseAddress = new Uri("http://localhost/") };
        using var captivePortalHttp = new HttpClient(new InProcSyncHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<!DOCTYPE html><html><body><h1>Public Wi-Fi Login</h1></body></html>", Encoding.UTF8, "text/html")
            }))) { BaseAddress = new Uri("http://localhost/") };
        using var healthyHttp = new HttpClient(new InProcSyncHandler(_ =>
            Task.FromResult(JsonOk(new { Status = "ONLINE", Timestamp = BusinessDayClock.UtcNow(), Message = "Offline sync endpoint is ready." }))))
        { BaseAddress = new Uri("http://localhost/") };

        Assert(await new PosConnectivityService(unreachableHttp).CheckConnectivityAsync() == TerminalConnectivityState.InternetConnectedApiUnavailable,
            "HttpRequestException must classify as InternetConnectedApiUnavailable.");
        Assert(await new PosConnectivityService(timeoutHttp).CheckConnectivityAsync() == TerminalConnectivityState.InternetConnectedApiUnavailable,
            "TaskCanceledException must classify as InternetConnectedApiUnavailable.");
        Assert(await new PosConnectivityService(dbDownHttp).CheckConnectivityAsync() == TerminalConnectivityState.ApiReachableDbUnavailable,
            "503 DB_UNAVAILABLE from health check must classify as ApiReachableDbUnavailable.");
        Assert(await new PosConnectivityService(captivePortalHttp).CheckConnectivityAsync() == TerminalConnectivityState.InternetConnectedApiUnavailable,
            "200 OK HTML captive portal response must NEVER report OnlineAndHealthy.");
        Assert(await new PosConnectivityService(healthyHttp).CheckConnectivityAsync() == TerminalConnectivityState.OnlineAndHealthy,
            "200 OK JSON Status=ONLINE must classify as OnlineAndHealthy.");

        // 3. Verify ShiftSession.VerifyPaymentShiftLocalFirstAsync prepares offline checkout cleanly under all disconnected/degraded health states with OFFLINE-SESSION-TOKEN
        foreach (var (label, client) in new[]
        {
            ("HttpRequestException", unreachableHttp),
            ("TaskCanceledException", timeoutHttp),
            ("503 DB_UNAVAILABLE", dbDownHttp),
            ("Captive Portal HTML 200", captivePortalHttp)
        })
        {
            var verify = await ShiftSession.VerifyPaymentShiftLocalFirstAsync(client, session, new PosConnectivityService(client));
            Assert(verify.IsReady && verify.Offline && verify.Issue == ShiftVerificationIssue.None && verify.Shift?.ClientShiftId == "shift-offline-01",
                $"VerifyPaymentShiftLocalFirstAsync with OFFLINE-SESSION-TOKEN must succeed locally on {label} (got Ready={verify.IsReady}, Offline={verify.Offline}, Issue={verify.Issue}, Err={verify.ErrorMessage}).");
        }

        // 4. Execute offline sale via OfflineCheckoutService.ProcessSaleAsync & verify persistence + receipt
        using var localDb = new LocalPosDbContext();
        var checkout = new OfflineCheckoutService(localDb, session);
        var sale = await checkout.ProcessSaleAsync(
            BuildSingleCashOrderDto(-1, "shift-offline-01", "cashier1"),
            amountTendered: 250m,
            paymentMethod: "CASH",
            referenceNumber: null,
            cashierId: "cashier1");

        Assert(sale.Success && !string.IsNullOrWhiteSpace(sale.InvoiceNumber),
            $"Offline ProcessSaleAsync must succeed cleanly: {sale.ErrorMessage}");

        using var verifyDb = new LocalPosDbContext();
        var order = await verifyDb.Orders.AsNoTracking().SingleAsync();
        int itemCount = await verifyDb.OrderItems.CountAsync(i => i.OrderId == order.OrderId);
        var payment = await verifyDb.Payments.AsNoTracking().SingleAsync();
        var outbox = await verifyDb.SyncOutboxes.AsNoTracking().SingleAsync(o => o.EntityType == "Order");
        var shiftRow = await verifyDb.CashDrawerShifts.AsNoTracking().SingleAsync();

        Assert(order.PaymentStatus == "PAID" && order.OrderStatus == "COMPLETED" && order.InvoiceNumber == sale.InvoiceNumber && itemCount == 1,
            "Saved offline order must be PAID/COMPLETED with 1 OrderItem and matching InvoiceNumber.");
        Assert(payment.PaymentMethod == "CASH" && payment.Amount == 224m && payment.TenderedAmount == 250m && payment.ChangeAmount == 26m,
            "Saved offline payment must preserve amount, tendered amount, and change.");
        Assert(outbox.Status == "PENDING" && outbox.EntityId == order.OrderId,
            "Offline sale must queue PENDING Order outbox item.");
        Assert(shiftRow.TotalCashSales == 224m && shiftRow.ExpectedBalance == 1224m,
            $"Offline shift cash totals must update cleanly (Sales={shiftRow.TotalCashSales}, Expected={shiftRow.ExpectedBalance}).");

        var receipt = await OfflineCheckoutService.LoadSavedReceiptFromDbAsync(verifyDb, order.OrderId, session);
        Assert(receipt != null && receipt.InvoiceNumber == sale.InvoiceNumber && receipt.Items.Count == 1,
            "LoadSavedReceiptFromDbAsync must hydrate saved offline receipt accurately.");
    }

    // OFFLINE-02: Mid-session drop & regression proof that vague error is never thrown
    private static async Task TestOffline02_MidSessionDropAndNoVagueError()
    {
        using var scope = new TempLocalDbScope("off02");
        SetDeterministicManilaClock(new DateTime(2026, 11, 14, 6, 30, 0, DateTimeKind.Utc));

        await SeedOfflineBaselineAsync(configureOpeningFund: true, openingFund: 1500m);
        string onlineJwt = CreateSyntheticJwt("cashier1", "Cashier", 1, 1);
        var session = CreateOfflineTestSession(onlineJwt, "cashier1", "Cashier");

        static HttpClient BuildDropHttpClient(string mode)
        {
            var http = new HttpClient(new InProcSyncHandler(req =>
            {
                string path = req.RequestUri?.AbsolutePath ?? string.Empty;
                if (path.EndsWith("api/offline-sync/health", StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(JsonOk(new { Status = "ONLINE", Timestamp = BusinessDayClock.UtcNow() }));
                }

                if (path.Contains("terminal-lock", StringComparison.OrdinalIgnoreCase) || path.Contains("lock-status", StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(JsonOk(new TerminalClosingLockDto
                    {
                        TerminalId = 1,
                        IsTerminalLocked = false,
                        IsCashierRestricted = false,
                        CurrentBusinessDate = "2026-11-14"
                    }));
                }

                if (path.Contains("opening-fund-config", StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(JsonOk(new TerminalCashFundConfigDto
                    {
                        TerminalId = 1,
                        BranchId = 1,
                        OutletId = 1,
                        DefaultOpeningFund = 1500m
                    }));
                }

                return mode switch
                {
                    "500" => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("Internal error") }),
                    "503" => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("Service unavailable") }),
                    "408" => Task.FromResult(new HttpResponseMessage(HttpStatusCode.RequestTimeout) { Content = new StringContent("Timeout") }),
                    "HttpReqEx" => throw new HttpRequestException("Mid-session TCP reset"),
                    "TaskCanceled" => throw new TaskCanceledException("Mid-session socket timeout"),
                    "JsonEx" => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>Not JSON</html>", Encoding.UTF8, "text/html") }),
                    _ => throw new InvalidOperationException($"Unknown mode {mode}")
                };
            }))
            {
                BaseAddress = new Uri("http://localhost/")
            };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateSyntheticJwt("cashier1", "Cashier", 1, 1));
            return http;
        }

        string[] failureModes = ["500", "503", "408", "HttpReqEx", "TaskCanceled", "JsonEx"];

        // 1. With NO active local open shift, transient failures must NEVER emit "Unable to verify current shift."
        foreach (string mode in failureModes)
        {
            using var http = BuildDropHttpClient(mode);

            string? curMsg = null;
            LocalCashDrawerShift? curNoCache = null;
            try
            {
                curNoCache = await ShiftSession.CurrentAsync(http, 1, offline: false);
            }
            catch (Exception ex)
            {
                curMsg = ex.Message;
            }
            Assert(curNoCache == null && (curMsg == null || !curMsg.Contains("Unable to verify current shift.", StringComparison.OrdinalIgnoreCase)),
                $"CurrentAsync({mode}) without cache must fall back to null and never emit vague error message (got: {curMsg}).");

            string? dtoMsg = null;
            CashDrawerShiftDto? dtoNoCache = null;
            try
            {
                dtoNoCache = await ShiftSession.CurrentDtoAsync(http, 1, offline: false);
            }
            catch (Exception ex)
            {
                dtoMsg = ex.Message;
            }
            Assert(dtoNoCache == null && (dtoMsg == null || !dtoMsg.Contains("Unable to verify current shift.", StringComparison.OrdinalIgnoreCase)),
                $"CurrentDtoAsync({mode}) without cache must fall back to null and never emit vague error message (got: {dtoMsg}).");

            var verifyNoShift = await ShiftSession.VerifyPaymentShiftLocalFirstAsync(http, session, new PosConnectivityService(http));
            Assert(!verifyNoShift.IsReady &&
                   verifyNoShift.Issue == ShiftVerificationIssue.NoActiveShift &&
                   (verifyNoShift.ErrorMessage == null || !verifyNoShift.ErrorMessage.Contains("Unable to verify current shift.", StringComparison.OrdinalIgnoreCase)),
                $"VerifyPaymentShiftLocalFirstAsync({mode}) must classify as NoActiveShift and never return vague message (got Issue={verifyNoShift.Issue}, Err={verifyNoShift.ErrorMessage}).");
        }

        // 2. Mid-session drop on OpenAsync(!offline) falls back to idempotent offline SQLite SHIFT_OPEN
        using (var dropOpenHttp = BuildDropHttpClient("HttpReqEx"))
        {
            var openedOfflineFallback = await ShiftSession.OpenAsync(dropOpenHttp, new OpenShiftDto
            {
                BranchId = 1,
                TerminalId = 1,
                OpeningFloat = 1500m,
                ClientShiftId = "shift-drop-open-1"
            }, "cashier1", offline: false);

            Assert(openedOfflineFallback.ShiftId < 0 && openedOfflineFallback.Status == "OPEN" && openedOfflineFallback.OpeningConfirmed,
                "OpenAsync(!offline) dropping mid-request must persist offline negative shift cleanly.");
        }

        // 3. With local active open shift cached, ALL 6 transient drop modes fall back locally without throwing
        foreach (string mode in failureModes)
        {
            using var http = BuildDropHttpClient(mode);

            var cur = await ShiftSession.CurrentAsync(http, 1, offline: false);
            Assert(cur != null && cur.ClientShiftId == "shift-drop-open-1",
                $"CurrentAsync({mode}) must return cached open shift on mid-session drop.");

            var curDto = await ShiftSession.CurrentDtoAsync(http, 1, offline: false);
            Assert(curDto != null && curDto.ClientShiftId == "shift-drop-open-1",
                $"CurrentDtoAsync({mode}) must return cached open shift DTO on mid-session drop.");

            var verifyWithShift = await ShiftSession.VerifyPaymentShiftLocalFirstAsync(http, session, new PosConnectivityService(http));
            Assert(verifyWithShift.IsReady && verifyWithShift.Offline && verifyWithShift.Issue == ShiftVerificationIssue.None &&
                   verifyWithShift.Shift?.ClientShiftId == "shift-drop-open-1",
                $"VerifyPaymentShiftLocalFirstAsync({mode}) must recover cached shift offline on mid-session drop.");
        }

        // 4. Mid-session drop on CloseAsync(!offline) falls back to idempotent offline SHIFT_CLOSE outbox
        using (var dropCloseHttp = BuildDropHttpClient("503"))
        {
            var closed = await ShiftSession.CloseAsync(dropCloseHttp, new CloseShiftDto
            {
                ShiftId = -1,
                ClientShiftId = "shift-drop-open-1",
                ClientCloseId = "close-drop-1",
                BranchId = 1,
                TerminalId = 1,
                CashierId = "cashier1",
                ActualCountedBalance = 1500m
            }, "cashier1", offline: false);

            Assert(closed.Status == "CLOSED" && closed.ClientCloseId == "close-drop-1",
                "CloseAsync(!offline) returning 503 must close locally and preserve ClientCloseId.");

            using var verifyDb = new LocalPosDbContext();
            var closeOutbox = await verifyDb.SyncOutboxes.AsNoTracking().SingleAsync(o => o.EntityType == "SHIFT_CLOSE");
            Assert(closeOutbox.EntityId == "close-drop-1" && closeOutbox.Status == "PENDING",
                "Mid-session drop CloseAsync must enqueue PENDING SHIFT_CLOSE outbox item.");
        }
    }

    // OFFLINE-03: App restart persistence while offline + sequential invoices + ShiftCacheRegressionTests
    private static async Task TestOffline03_RestartPersistenceAndSequentialInvoices()
    {
        // Part 1: Persisted offline shift, sale, and sequential BIR invoices across simulated app restarts
        using (var scope = new TempLocalDbScope("off03"))
        {
            SetDeterministicManilaClock(new DateTime(2026, 11, 14, 7, 0, 0, DateTimeKind.Utc));
            await SeedOfflineBaselineAsync(configureOpeningFund: true, openingFund: 1000m, lastInvoiceNumber: 0);

            using var offlineHttp = new HttpClient(new InProcSyncHandler(_ => throw new HttpRequestException("Disconnected"))) { BaseAddress = new Uri("http://localhost/") };

            // Boot 1: Open shift offline and ring up Sale #1
            var opened = await ShiftSession.OpenAsync(offlineHttp, new OpenShiftDto
            {
                BranchId = 1,
                TerminalId = 1,
                OpeningFloat = 1000m,
                ClientShiftId = "shift-restart-1"
            }, "cashier1", offline: true);
            Assert(opened.ShiftId < 0, "Offline shift must receive negative temporary ShiftId.");

            var session1 = CreateOfflineTestSession("OFFLINE-SESSION-TOKEN", "cashier1");
            using (var db1 = new LocalPosDbContext())
            {
                var sale1 = await new OfflineCheckoutService(db1, session1).ProcessSaleAsync(
                    BuildSingleCashOrderDto(opened.ShiftId, opened.ClientShiftId, "cashier1"), 224m, "CASH", null, "cashier1");
                Assert(sale1.Success && sale1.InvoiceNumber == "TXN1-0000001",
                    $"First offline sale expected TXN1-0000001 (got {sale1.InvoiceNumber}, err={sale1.ErrorMessage}).");
            }

            // Boot 2 (simulate app restart while offline: fresh session, fresh DbContext, fresh connectivity service)
            var session2 = CreateOfflineTestSession("OFFLINE-SESSION-TOKEN", "cashier1");
            var verify2 = await ShiftSession.VerifyPaymentShiftLocalFirstAsync(offlineHttp, session2, new PosConnectivityService(offlineHttp));
            Assert(verify2.IsReady && verify2.Offline && verify2.Issue == ShiftVerificationIssue.None &&
                   verify2.Shift != null && verify2.Shift.ShiftId == opened.ShiftId && verify2.Shift.TotalCashSales == 224m,
                "After restart while offline, VerifyPaymentShiftLocalFirstAsync must recover open shift and cash sales.");

            using (var db2 = new LocalPosDbContext())
            {
                var sale2 = await new OfflineCheckoutService(db2, session2).ProcessSaleAsync(
                    BuildSingleCashOrderDto(verify2.Shift!.ShiftId, verify2.Shift.ClientShiftId, "cashier1"), 224m, "CASH", null, "cashier1");
                Assert(sale2.Success && sale2.InvoiceNumber == "TXN1-0000002",
                    $"Second sale after restart expected TXN1-0000002 (got {sale2.InvoiceNumber}, err={sale2.ErrorMessage}).");
            }

            // Boot 3 (another restart while offline): ring Sales #3 and #4
            var session3 = CreateOfflineTestSession("OFFLINE-SESSION-TOKEN", "cashier1");
            var verify3 = await ShiftSession.VerifyPaymentShiftLocalFirstAsync(offlineHttp, session3, new PosConnectivityService(offlineHttp));
            Assert(verify3.IsReady && verify3.Shift?.TotalCashSales == 448m,
                "Second restart must reflect accumulated cash sales (448.00).");

            using (var db3 = new LocalPosDbContext())
            {
                var co3 = new OfflineCheckoutService(db3, session3);
                var sale3 = await co3.ProcessSaleAsync(BuildSingleCashOrderDto(verify3.Shift!.ShiftId, verify3.Shift.ClientShiftId, "cashier1"), 224m, "CASH", null, "cashier1");
                var sale4 = await co3.ProcessSaleAsync(BuildSingleCashOrderDto(verify3.Shift.ShiftId, verify3.Shift.ClientShiftId, "cashier1"), 224m, "CASH", null, "cashier1");
                Assert(sale3.Success && sale3.InvoiceNumber == "TXN1-0000003", $"Expected TXN1-0000003 (got {sale3.InvoiceNumber}).");
                Assert(sale4.Success && sale4.InvoiceNumber == "TXN1-0000004", $"Expected TXN1-0000004 (got {sale4.InvoiceNumber}).");
            }

            using var auditDb = new LocalPosDbContext();
            var orders = await auditDb.Orders.AsNoTracking().OrderBy(o => o.InvoiceNumber).ToListAsync();
            var seq = await auditDb.InvoiceSequences.AsNoTracking().SingleAsync(s => s.TerminalId == 1);
            var outboxOrders = await auditDb.SyncOutboxes.AsNoTracking().CountAsync(o => o.EntityType == "Order" && o.Status == "PENDING");
            var shiftEnd = await auditDb.CashDrawerShifts.AsNoTracking().SingleAsync();

            Assert(orders.Count == 4 && outboxOrders == 4 && seq.LastIssuedNumber == 4,
                "All 4 offline orders must persist sequentially with PENDING outbox entries.");
            Assert(orders.Select(o => o.InvoiceNumber).SequenceEqual(new[] { "TXN1-0000001", "TXN1-0000002", "TXN1-0000003", "TXN1-0000004" }),
                "Offline invoice numbers across restarts must have zero gaps and zero duplicates.");
            Assert(shiftEnd.TotalCashSales == 896m && shiftEnd.ExpectedBalance == 1896m,
                $"Accumulated offline shift sales must equal 896.00 and expected balance 1896.00 (actual: {shiftEnd.TotalCashSales} / {shiftEnd.ExpectedBalance}).");
        }

        // Part 2: Execute ShiftCacheRegressionTests in its own isolated temp SQLite database
        using (var shiftRegScope = new TempLocalDbScope("shiftreg", setShiftRegressionEnv: true))
        {
            await ShiftCacheRegressionTests.RunAsync();
        }
    }

    // OFFLINE-04: Offline shift open + movements + reconnect sync + idempotent replay + online close protection
    private static async Task TestOffline04_OfflineShiftOpenMovementsReconnectIdempotentSync()
    {
        using var scope = new TempLocalDbScope("off04");
        var dayUtc = new DateTime(2026, 11, 14, 6, 0, 0, DateTimeKind.Utc); // 14:00 Manila (2026-11-14 business day)
        SetDeterministicManilaClock(dayUtc);

        var (conn, serverDb, initialServerShift) = await CreateTestEnvAsync();
        await using var cleanup = conn;

        // Ensure prior server shift belongs to yesterday's business day and is already closed
        initialServerShift.Status = "CLOSED";
        initialServerShift.OpenedAt = new DateTime(2026, 11, 13, 2, 0, 0, DateTimeKind.Utc);
        initialServerShift.ClosedAt = new DateTime(2026, 11, 13, 10, 0, 0, DateTimeKind.Utc);

        serverDb.Categories.Add(new Category { CategoryId = 1, Name = "Meals", IsActive = true });
        serverDb.Products.Add(new Product { ProductId = 1, CategoryId = 1, Name = "Lechon Meal", ProductType = "SIMPLE", Price = 224m, IsActive = true });
        serverDb.InvoiceSequences.Add(new InvoiceSequence
        {
            TerminalId = 1,
            SeriesCode = "TXN1",
            Prefix = "TXN1-",
            StartingNumber = 1,
            EndingNumber = 10000,
            LastIssuedNumber = 0,
            PaddingLength = 7,
            IsActive = true
        });
        await serverDb.SaveChangesAsync();

        await SeedOfflineBaselineAsync(configureOpeningFund: true, openingFund: 1000m, lastInvoiceNumber: 0);

        // 1. Open negative offline shift, process offline cash sale, record offline CASH_IN and PETTY_CASH
        using var noNetHttp = new HttpClient(new InProcSyncHandler(_ => throw new HttpRequestException("Offline"))) { BaseAddress = new Uri("http://localhost/") };
        var session = CreateOfflineTestSession(CreateSyntheticJwt("cashier1", "Cashier"), "cashier1", "Cashier");

        var offlineShift = await ShiftSession.OpenAsync(noNetHttp, new OpenShiftDto
        {
            BranchId = 1,
            TerminalId = 1,
            OpeningFloat = 1000m,
            ClientShiftId = "shift-offline-mov-1"
        }, "cashier1", offline: true);
        Assert(offlineShift.ShiftId < 0, "Offline shift open must assign negative ShiftId.");

        using (var localDb = new LocalPosDbContext())
        {
            var checkout = new OfflineCheckoutService(localDb, session);
            var sale = await checkout.ProcessSaleAsync(
                BuildSingleCashOrderDto(offlineShift.ShiftId, offlineShift.ClientShiftId, "cashier1"),
                224m, "CASH", null, "cashier1");
            Assert(sale.Success, $"Offline sale under offline shift failed: {sale.ErrorMessage}");
        }

        // Persist offline CASH_IN (500.00) and PETTY_CASH (150.00 with verified manager1 PIN hash) + outbox
        string cashInId = "cmv-in-001";
        string pettyId = "cmv-out-001";
        string managerPinHash = ComputeUpperSha256Hex("9999");

        using (var movDb = new LocalPosDbContext())
        {
            var shiftEntity = await movDb.CashDrawerShifts.SingleAsync(s => s.ShiftId == offlineShift.ShiftId);

            var movIn = new LocalCashDrawerMovement
            {
                ClientMovementId = cashInId,
                ShiftId = offlineShift.ShiftId,
                BranchId = 1,
                TerminalId = 1,
                MovementType = "CASH_IN",
                CategoryId = 1,
                CategoryName = "Additional Float",
                Amount = 500m,
                Reason = "Mid-day cash top up",
                ReferenceNumber = "OFF-IN-01",
                Status = "POSTED",
                CreatedBy = "cashier1",
                AuthorizedBy = "cashier1",
                CreatedAt = BusinessDayClock.UtcNow(),
                PostedAt = BusinessDayClock.UtcNow(),
                SyncStatus = "PENDING"
            };

            var movOut = new LocalCashDrawerMovement
            {
                ClientMovementId = pettyId,
                ShiftId = offlineShift.ShiftId,
                BranchId = 1,
                TerminalId = 1,
                MovementType = "PETTY_CASH",
                CategoryId = 2,
                CategoryName = "Ice & Store Supplies",
                Amount = 150m,
                Reason = "Store ice purchase",
                ReferenceNumber = "OFF-OUT-01",
                Status = "POSTED",
                CreatedBy = "cashier1",
                AuthorizedBy = "manager1",
                ApprovedBy = "manager1",
                SupervisorPinHash = managerPinHash,
                CreatedAt = BusinessDayClock.UtcNow().AddSeconds(10),
                ApprovedAt = BusinessDayClock.UtcNow().AddSeconds(10),
                PostedAt = BusinessDayClock.UtcNow().AddSeconds(10),
                SyncStatus = "PENDING"
            };

            shiftEntity.TotalCashIn += 500m;
            shiftEntity.TotalCashOut += 150m;
            shiftEntity.ExpectedBalance = shiftEntity.OpeningFloat + shiftEntity.TotalCashSales + shiftEntity.TotalCashIn - shiftEntity.TotalCashOut;

            movDb.CashDrawerMovements.AddRange(movIn, movOut);
            movDb.SyncOutboxes.AddRange(
                new LocalSyncOutbox
                {
                    EntityType = "CASH_MOVEMENT",
                    EntityId = cashInId,
                    PayloadJson = JsonSerializer.Serialize(new OfflineCashMovementSyncRequest
                    {
                        ClientMovementId = cashInId,
                        ShiftId = offlineShift.ShiftId,
                        ClientShiftId = offlineShift.ClientShiftId,
                        BranchId = 1,
                        TerminalId = 1,
                        MovementType = "CASH_IN",
                        CategoryId = 1,
                        CategoryName = "Additional Float",
                        Amount = 500m,
                        Reason = "Mid-day cash top up",
                        ReferenceNumber = "OFF-IN-01",
                        Status = "POSTED",
                        CreatedBy = "cashier1",
                        CreatedAt = movIn.CreatedAt,
                        PostedAt = movIn.PostedAt
                    }),
                    Status = "PENDING",
                    CreatedAt = BusinessDayClock.UtcNow()
                },
                new LocalSyncOutbox
                {
                    EntityType = "CASH_MOVEMENT",
                    EntityId = pettyId,
                    PayloadJson = JsonSerializer.Serialize(new OfflineCashMovementSyncRequest
                    {
                        ClientMovementId = pettyId,
                        ShiftId = offlineShift.ShiftId,
                        ClientShiftId = offlineShift.ClientShiftId,
                        BranchId = 1,
                        TerminalId = 1,
                        MovementType = "PETTY_CASH",
                        CategoryId = 2,
                        CategoryName = "Ice & Store Supplies",
                        Amount = 150m,
                        Reason = "Store ice purchase",
                        ReferenceNumber = "OFF-OUT-01",
                        Status = "POSTED",
                        CreatedBy = "cashier1",
                        ApprovedBy = "manager1",
                        SupervisorPinHash = managerPinHash,
                        CreatedAt = movOut.CreatedAt,
                        ApprovedAt = movOut.ApprovedAt,
                        PostedAt = movOut.PostedAt
                    }),
                    Status = "PENDING",
                    CreatedAt = BusinessDayClock.UtcNow().AddSeconds(10)
                });
            await movDb.SaveChangesAsync();
        }

        // 2. Build in-proc server handler supporting CashDrawerController, OfflineSyncController & ReferenceData
        var syncCtrl = CreateTerminalSyncController(serverDb);
        var cashCtrl = CreateController(serverDb, "POS-1", "Terminal");
        bool returnStaleOpenInRefSync = false;

        using var inProcHttp = new HttpClient(new InProcSyncHandler(async req =>
        {
            string path = req.RequestUri!.AbsolutePath;
            var jsonOpts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

            if (path.EndsWith("api/offline-sync/health", StringComparison.OrdinalIgnoreCase))
            {
                return ObjectResultToHttpResponse(await syncCtrl.GetSyncHealth());
            }

            if (path.EndsWith("api/cash-drawer/open-shift", StringComparison.OrdinalIgnoreCase))
            {
                var dto = JsonSerializer.Deserialize<OpenShiftDto>(await req.Content!.ReadAsStringAsync(), jsonOpts)!;
                return ObjectResultToHttpResponse(await cashCtrl.OpenShift(dto));
            }

            if (path.EndsWith("api/cash-drawer/close-shift", StringComparison.OrdinalIgnoreCase))
            {
                var dto = JsonSerializer.Deserialize<CloseShiftDto>(await req.Content!.ReadAsStringAsync(), jsonOpts)!;
                return ObjectResultToHttpResponse(await cashCtrl.CloseShift(dto));
            }

            if (path.Equals("/api/cash-drawer/current", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("/api/cash-drawer/current/", StringComparison.OrdinalIgnoreCase))
            {
                return ActionResultToHttpResponse(await cashCtrl.GetCurrentShift(1));
            }

            if (path.StartsWith("/api/cash-drawer/terminal-lock", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("/api/cash-drawer/lock-status", StringComparison.OrdinalIgnoreCase))
            {
                return ActionResultToHttpResponse(await cashCtrl.GetTerminalLockStatus(1, "cashier1"));
            }

            if (path.EndsWith("api/offline-sync/transactions", StringComparison.OrdinalIgnoreCase))
            {
                var dto = JsonSerializer.Deserialize<OfflineTransactionSyncRequest>(await req.Content!.ReadAsStringAsync(), jsonOpts)!;
                return ActionResultToHttpResponse(await syncCtrl.SyncTransaction(dto));
            }

            if (path.EndsWith("api/offline-sync/cash-movements", StringComparison.OrdinalIgnoreCase))
            {
                var dto = JsonSerializer.Deserialize<OfflineCashMovementSyncRequest>(await req.Content!.ReadAsStringAsync(), jsonOpts)!;
                return ActionResultToHttpResponse(await syncCtrl.SyncCashMovement(dto));
            }

            if (path.EndsWith("api/offline/reference-data", StringComparison.OrdinalIgnoreCase))
            {
                var srvShift = await serverDb.CashDrawerShifts.AsNoTracking().OrderByDescending(s => s.ShiftId).FirstOrDefaultAsync();
                var ackedIds = srvShift != null
                    ? await (from rec in serverDb.OfflineSyncIdempotencyRecords.AsNoTracking()
                             join ord in serverDb.Orders.AsNoTracking() on rec.ServerOrderId equals ord.OrderId
                             where ord.TerminalId == srvShift.TerminalId
                             select rec.LocalTransactionId).ToListAsync()
                    : new List<string>();
                CashDrawerShiftDto? activeDto = returnStaleOpenInRefSync && srvShift != null
                    ? new CashDrawerShiftDto
                    {
                        ShiftId = srvShift.ShiftId,
                        ClientShiftId = srvShift.ClientShiftId,
                        BranchId = srvShift.BranchId,
                        TerminalId = srvShift.TerminalId,
                        CashierId = srvShift.CashierId,
                        OpeningFloat = srvShift.OpeningFloat,
                        OpeningConfirmed = srvShift.OpeningConfirmed,
                        ExpectedBalance = srvShift.ExpectedBalance,
                        TotalCashSales = srvShift.TotalCashSales,
                        TotalCashIn = srvShift.TotalCashIn,
                        TotalCashOut = srvShift.TotalCashOut,
                        AcknowledgedLocalTransactionIds = ackedIds,
                        Status = "OPEN",
                        OpenedAt = srvShift.OpenedAt
                    }
                    : null;

                return JsonOk(new OfflineReferenceDataResponseDto
                {
                    Version = "1.0",
                    ServerTimestamp = BusinessDayClock.UtcNow(),
                    Branch = new OfflineBranchConfigDto { BranchId = 1, Name = "Main Branch", Code = "BR-01", TaxType = "VAT-Registered", VatDisplay = true },
                    Terminal = new OfflineTerminalConfigDto
                    {
                        TerminalId = 1,
                        OutletId = 1,
                        OutletName = "Counter 1",
                        TerminalCode = "POS-1",
                        TerminalName = "POS1",
                        BranchId = 1,
                        BranchName = "Main Branch",
                        BranchCode = "BR-01",
                        BranchTaxType = "VAT-Registered",
                        VatDisplay = true,
                        DeviceIdentifier = "DEV-UUID-1"
                    },
                    InvoiceSequence = new OfflineInvoiceSequenceDto
                    {
                        SequenceId = 1,
                        TerminalId = 1,
                        SeriesCode = "TXN1",
                        Prefix = "TXN1-",
                        StartingNumber = 1,
                        EndingNumber = 10000,
                        LastIssuedNumber = 1,
                        PaddingLength = 7,
                        IsActive = true
                    },
                    Cashiers =
                    [
                        new OfflineCashierDto { UserId = 1, Username = "cashier1", Role = "Cashier", PinHash = ComputeUpperSha256Hex("1111") },
                        new OfflineCashierDto { UserId = 3, Username = "manager1", Role = "Manager", PinHash = ComputeUpperSha256Hex("9999") }
                    ],
                    ActiveShift = activeDto
                });
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }))
        {
            BaseAddress = new Uri("http://localhost/")
        };
        inProcHttp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.Token);

        // 3. Reconnect sync: verify strict priority order and child migration upon SHIFT_OPEN acknowledgement
        using var syncLocalDb = new LocalPosDbContext();
        var syncSvc = new OfflineTransactionSyncService(syncLocalDb);
        var queue = await syncSvc.GetPendingOutboxItemsAsync();
        Assert(queue.Count == 4 &&
               queue[0].EntityType == "SHIFT_OPEN" &&
               queue[1].EntityType == "Order" &&
               queue[2].EntityType == "CASH_MOVEMENT" &&
               queue[3].EntityType == "CASH_MOVEMENT",
            $"Outbox queue must prioritize SHIFT_OPEN -> Order -> CASH_MOVEMENT (got: {string.Join(",", queue.Select(x => x.EntityType))}).");

        // Upload SHIFT_OPEN first
        Assert(await syncSvc.UploadTransactionAsync(queue[0], inProcHttp), "SHIFT_OPEN outbox upload must succeed.");

        var serverOpenedShift = await serverDb.CashDrawerShifts.AsNoTracking().SingleAsync(s => s.ClientShiftId == offlineShift.ClientShiftId);
        Assert(serverOpenedShift.ShiftId > 1, "Server must assign positive ShiftId for synced offline shift.");

        using (var postOpenCheckDb = new LocalPosDbContext())
        {
            var localPositiveShift = await postOpenCheckDb.CashDrawerShifts.AsNoTracking()
                .SingleAsync(s => s.ClientShiftId == offlineShift.ClientShiftId && s.Status == "OPEN");
            var retiredNegativeShift = await postOpenCheckDb.CashDrawerShifts.AsNoTracking()
                .SingleAsync(s => s.ShiftId == offlineShift.ShiftId);
            var migratedOrder = await postOpenCheckDb.Orders.AsNoTracking().SingleAsync();
            var migratedMovements = await postOpenCheckDb.CashDrawerMovements.AsNoTracking().ToListAsync();

            Assert(retiredNegativeShift.Status == "CLOSED",
                $"Negative local placeholder must be retired to CLOSED upon SHIFT_OPEN sync (got {retiredNegativeShift.Status}).");
            Assert(localPositiveShift.ShiftId == serverOpenedShift.ShiftId,
                $"Local shift must migrate to server ShiftId {serverOpenedShift.ShiftId} (got {localPositiveShift.ShiftId}).");
            Assert(migratedOrder.ShiftId == serverOpenedShift.ShiftId,
                $"Child LocalOrder.ShiftId must migrate to {serverOpenedShift.ShiftId} (got {migratedOrder.ShiftId}).");
            Assert(migratedMovements.All(m => m.ShiftId == serverOpenedShift.ShiftId),
                "All child LocalCashDrawerMovement rows must migrate from negative ShiftId to positive server ShiftId.");
            Assert(localPositiveShift.TotalCashIn == 500m && localPositiveShift.TotalCashOut == 150m && localPositiveShift.TotalCashSales == 224m,
                "Unsynced local sales & movement totals must be preserved after SHIFT_OPEN cache merge.");

            // Apply stale server snapshot (0 movement/sales totals) before child items finish uploading -> totals must stay preserved
            var staleServerZeroDto = new CashDrawerShiftDto
            {
                ShiftId = serverOpenedShift.ShiftId,
                ClientShiftId = serverOpenedShift.ClientShiftId,
                BranchId = 1,
                TerminalId = 1,
                CashierId = "cashier1",
                OpeningFloat = 1000m,
                OpeningConfirmed = true,
                TotalCashSales = 0m,
                TotalCashIn = 0m,
                TotalCashOut = 0m,
                ExpectedBalance = 1000m,
                AcknowledgedLocalTransactionIds = new List<string>(),
                Status = "OPEN",
                OpenedAt = serverOpenedShift.OpenedAt
            };
            await ShiftSession.ApplySnapshotAsync(postOpenCheckDb, staleServerZeroDto, reconcileCurrent: true);
            await postOpenCheckDb.SaveChangesAsync();

            var preservedShift = await postOpenCheckDb.CashDrawerShifts.AsNoTracking().SingleAsync(s => s.ShiftId == serverOpenedShift.ShiftId);
            Assert(preservedShift.TotalCashSales == 224m && preservedShift.TotalCashIn == 500m && preservedShift.TotalCashOut == 150m,
                $"ApplySnapshotAsync must NOT wipe unsynced Order/CASH_MOVEMENT totals (Sales={preservedShift.TotalCashSales}, In={preservedShift.TotalCashIn}, Out={preservedShift.TotalCashOut}).");
        }

        // Upload Order, CASH_IN, PETTY_CASH
        Assert(await syncSvc.UploadTransactionAsync(queue[1], inProcHttp), "Queued Order upload must succeed.");
        Assert(await syncSvc.UploadTransactionAsync(queue[2], inProcHttp), "Queued CASH_IN movement upload must succeed.");
        Assert(await syncSvc.UploadTransactionAsync(queue[3], inProcHttp), "Queued PETTY_CASH movement upload must succeed.");

        var srvOrdersCount = await serverDb.Orders.CountAsync(o => o.ShiftId == serverOpenedShift.ShiftId);
        var srvMovements = await serverDb.CashDrawerMovements.AsNoTracking().Where(m => m.ShiftId == serverOpenedShift.ShiftId).ToListAsync();
        var syncedServerShiftState = await serverDb.CashDrawerShifts.AsNoTracking().SingleAsync(s => s.ShiftId == serverOpenedShift.ShiftId);
        Assert(srvOrdersCount == 1 &&
               srvMovements.Count == 3 &&
               srvMovements.All(m => m.Status == "POSTED") &&
               srvMovements.Any(m => m.ClientMovementId == cashInId && m.MovementType == "CASH_IN") &&
               srvMovements.Any(m => m.ClientMovementId == pettyId && m.MovementType == "PETTY_CASH") &&
               srvMovements.Any(m => m.MovementType == "CASH_SALE") &&
               syncedServerShiftState.ExpectedBalance == 1574m,
            $"Server must persist 1 Order, CASH_SALE + CASH_IN + PETTY_CASH POSTED movements, and ExpectedBalance=1574.00 (actual: orders={srvOrdersCount}, movs={srvMovements.Count}, balance={syncedServerShiftState.ExpectedBalance}).");

        using (var postSyncLocal = new LocalPosDbContext())
        {
            var localMovs = await postSyncLocal.CashDrawerMovements.AsNoTracking().ToListAsync();
            Assert(localMovs.All(m => m.SyncStatus == "SYNCED"), "Local cash movements must transition to SYNCED.");
        }

        // Replay all synced outbox items -> idempotent without duplicate shifts, orders, or movements
        foreach (var item in queue)
        {
            Assert(await syncSvc.UploadTransactionAsync(item, inProcHttp), $"Idempotent replay of {item.EntityType} ({item.EntityId}) must succeed.");
        }
        Assert(await serverDb.CashDrawerShifts.CountAsync(s => s.ClientShiftId == offlineShift.ClientShiftId) == 1,
            "Replaying SHIFT_OPEN must not duplicate shifts.");
        Assert(await serverDb.Orders.CountAsync() == 1, "Replaying Order must not duplicate server orders.");
        Assert(await serverDb.CashDrawerMovements.CountAsync() == 3, "Replaying CASH_MOVEMENT items must not duplicate movements.");

        // 3b. Explicit ClientShiftId that does not exist must return 409 Conflict (never fall back onto unrelated OPEN shift),
        //     and mismatched non-Admin movement creator must be rejected.
        var unknownShiftMovRes = await syncCtrl.SyncCashMovement(new OfflineCashMovementSyncRequest
        {
            ClientMovementId = "mov-unknown-shift-01",
            ClientShiftId = "nonexistent-client-shift-id",
            BranchId = 1,
            TerminalId = 1,
            MovementType = "CASH_IN",
            Amount = 50m,
            Reason = "Unknown shift guard check",
            ReferenceNumber = "OFF-UNK-01",
            Status = "POSTED",
            CreatedBy = "cashier1",
            CreatedAt = BusinessDayClock.UtcNow()
        });
        Assert(unknownShiftMovRes.Result is ConflictObjectResult,
            "SyncCashMovement with explicit unknown ClientShiftId must return Conflict instead of attaching to an unrelated OPEN shift.");

        var wrongCashierMovRes = await syncCtrl.SyncCashMovement(new OfflineCashMovementSyncRequest
        {
            ClientMovementId = "mov-wrong-cashier-01",
            ShiftId = serverOpenedShift.ShiftId,
            ClientShiftId = offlineShift.ClientShiftId,
            BranchId = 1,
            TerminalId = 1,
            MovementType = "CASH_IN",
            Amount = 50m,
            Reason = "Cross-cashier ownership check",
            ReferenceNumber = "OFF-OWN-01",
            Status = "POSTED",
            CreatedBy = "cashier2",
            CreatedAt = BusinessDayClock.UtcNow()
        });
        Assert(wrongCashierMovRes.Result is ConflictObjectResult,
            "SyncCashMovement with CreatedBy belonging to another cashier under a Terminal token must return Conflict.");

        // 4. Online shift (ShiftId > 0) closed offline is NOT reopened by server "OPEN" snapshot & SHIFT_CLOSE "SYNCED" lock check
        var offlineClosedPositive = await ShiftSession.CloseAsync(noNetHttp, new CloseShiftDto
        {
            ShiftId = serverOpenedShift.ShiftId,
            ClientShiftId = offlineShift.ClientShiftId,
            ClientCloseId = "close-positive-offline-1",
            BranchId = 1,
            TerminalId = 1,
            CashierId = "cashier1",
            ActualCountedBalance = 1574m
        }, "cashier1", offline: true);
        Assert(offlineClosedPositive.Status == "CLOSED", "Offline close on positive shift must mark local row CLOSED.");

        // While SHIFT_CLOSE is still PENDING, GetTerminalLockStatusAsync(!offline) must keep local closing lock even though server still says unlocked
        var lockWhilePending = await ShiftSession.GetTerminalLockStatusAsync(inProcHttp, 1, "cashier1", offline: false);
        Assert(lockWhilePending.IsTerminalLocked || lockWhilePending.IsCashierRestricted,
            "Pending offline SHIFT_CLOSE must preserve local closing lock even while server is still unlocked.");

        // ReferenceDataSyncService fetches server snapshot that still says Status="OPEN" -> local shift MUST remain CLOSED
        returnStaleOpenInRefSync = true;
        using (var refDb = new LocalPosDbContext())
        {
            var refSvc = new ReferenceDataSyncService(refDb);
            bool refOk = await refSvc.SyncReferenceDataAsync(1, inProcHttp);
            Assert(refOk, "ReferenceDataSyncAsync must succeed against in-proc server.");
        }
        using (var checkClosedDb = new LocalPosDbContext())
        {
            var persistedClosed = await checkClosedDb.CashDrawerShifts.AsNoTracking().SingleAsync(s => s.ShiftId == serverOpenedShift.ShiftId);
            Assert(persistedClosed.Status == "CLOSED",
                $"Pending offline SHIFT_CLOSE must prevent stale server OPEN snapshot from reopening local shift (actual: {persistedClosed.Status}).");
        }

        // Upload pending SHIFT_CLOSE -> verify Status becomes SYNCED and lock status behaves accurately
        var pendingCloseItems = await syncSvc.GetPendingOutboxItemsAsync();
        Assert(pendingCloseItems.Count == 1 && pendingCloseItems[0].EntityType == "SHIFT_CLOSE", "Expected 1 pending SHIFT_CLOSE outbox item.");
        Assert(await syncSvc.UploadTransactionAsync(pendingCloseItems[0], inProcHttp), "SHIFT_CLOSE upload must succeed.");

        using (var verifySyncedCloseDb = new LocalPosDbContext())
        {
            var syncedCloseOutbox = await verifySyncedCloseDb.SyncOutboxes.AsNoTracking().SingleAsync(o => o.EntityType == "SHIFT_CLOSE");
            var closedLocal = await verifySyncedCloseDb.CashDrawerShifts.AsNoTracking().SingleAsync(s => s.ShiftId == serverOpenedShift.ShiftId);
            Assert(syncedCloseOutbox.Status == "SYNCED" && closedLocal.Status == "CLOSED",
                "SHIFT_CLOSE outbox must become SYNCED while shift stays CLOSED.");
        }

        // Same business day after SHIFT_CLOSE SYNCED -> closing cashier is restricted on Terminal 1 while Terminal 1 stays open for other cashiers; past next day 4:00 AM -> resets cleanly
        var lockToday = await ShiftSession.GetTerminalLockStatusAsync(inProcHttp, 1, "cashier1", offline: false);
        Assert(lockToday.IsCashierRestricted && !lockToday.IsTerminalLocked && lockToday.CurrentBusinessDate == "2026-11-14",
            $"After SHIFT_CLOSE SYNCED on same business day, closing cashier must be restricted without locking terminal for others (Restricted={lockToday.IsCashierRestricted}, TerminalLocked={lockToday.IsTerminalLocked}).");
        var lockCashier2Today = await ShiftSession.GetTerminalLockStatusAsync(inProcHttp, 1, "cashier2", offline: false);
        Assert(!lockCashier2Today.IsCashierRestricted && !lockCashier2Today.IsTerminalLocked,
            "Other cashiers must still be allowed on Terminal 1 after Cashier A closes their shift.");

        SetDeterministicManilaClock(new DateTime(2026, 11, 14, 20, 5, 0, DateTimeKind.Utc), tickCount64: 50_000L + (long)TimeSpan.FromHours(15).TotalMilliseconds);
        var lockNextDayOnline = await ShiftSession.GetTerminalLockStatusAsync(inProcHttp, 1, "cashier1", offline: false);
        Assert(!lockNextDayOnline.IsTerminalLocked && !lockNextDayOnline.IsCashierRestricted,
            $"After 4:00 AM next business day with SHIFT_CLOSE SYNCED, lock must clear cleanly online & locally (Locked={lockNextDayOnline.IsTerminalLocked}, Msg={lockNextDayOnline.LockMessage}).");
        var lockNextDayOffline = await ShiftSession.GetTerminalLockStatusAsync(noNetHttp, 1, "cashier1", offline: true);
        Assert(!lockNextDayOffline.IsTerminalLocked && !lockNextDayOffline.IsCashierRestricted,
            $"After 4:00 AM next business day, historical SYNCED SHIFT_CLOSE row must NOT cause lingering offline lock (Locked={lockNextDayOffline.IsTerminalLocked}, Msg={lockNextDayOffline.LockMessage}).");
    }

    // OFFLINE-05: Security fail-closed cases & ShiftVerificationIssue classification
    private static async Task TestOffline05_SecurityFailClosedAndErrorClassification()
    {
        var dayUtc = new DateTime(2026, 11, 14, 6, 0, 0, DateTimeKind.Utc); // 14:00 Manila

        // 1. InvalidSession
        using (var s1 = new TempLocalDbScope("off05_1"))
        {
            SetDeterministicManilaClock(dayUtc);
            var unauthSession = new PosSessionService();
            using var offlineHttp = new HttpClient(new InProcSyncHandler(_ => throw new HttpRequestException("Offline"))) { BaseAddress = new Uri("http://localhost/") };
            var res = await ShiftSession.VerifyPaymentShiftLocalFirstAsync(offlineHttp, unauthSession, new PosConnectivityService(offlineHttp));
            Assert(!res.IsReady && res.Issue == ShiftVerificationIssue.InvalidSession,
                $"Unauthenticated session must classify as InvalidSession (got {res.Issue}).");
        }

        // 2. MissingOpeningConfig vs 3. NoActiveShift
        using (var s2 = new TempLocalDbScope("off05_2"))
        {
            SetDeterministicManilaClock(dayUtc);
            await SeedOfflineBaselineAsync(configureOpeningFund: false);
            var session = CreateOfflineTestSession("OFFLINE-SESSION-TOKEN", "cashier1");
            using var offlineHttp = new HttpClient(new InProcSyncHandler(_ => throw new HttpRequestException("Offline"))) { BaseAddress = new Uri("http://localhost/") };

            var missingCfg = await ShiftSession.VerifyPaymentShiftLocalFirstAsync(offlineHttp, session, new PosConnectivityService(offlineHttp));
            Assert(!missingCfg.IsReady && missingCfg.Issue == ShiftVerificationIssue.MissingOpeningConfig,
                $"No shift + no float config must classify as MissingOpeningConfig (got {missingCfg.Issue}).");

            using (var cfgDb = new LocalPosDbContext())
            {
                var term = await cfgDb.TerminalConfigs.SingleAsync();
                term.DefaultOpeningFund = 1000m;
                term.EffectiveOpeningFund = 1000m;
                term.OpeningFundSource = "OUTLET_DEFAULT";
                await cfgDb.SaveChangesAsync();
            }

            var noActive = await ShiftSession.VerifyPaymentShiftLocalFirstAsync(offlineHttp, session, new PosConnectivityService(offlineHttp));
            Assert(!noActive.IsReady && noActive.Issue == ShiftVerificationIssue.NoActiveShift && noActive.OpeningFundConfig?.EffectiveOpeningFund == 1000m,
                $"No shift + configured float must classify as NoActiveShift (got {noActive.Issue}).");
        }

        // 4. OpeningNotConfirmed
        using (var s3 = new TempLocalDbScope("off05_3"))
        {
            SetDeterministicManilaClock(dayUtc);
            await SeedOfflineBaselineAsync(configureOpeningFund: true, openingFund: 1000m);
            using (var db = new LocalPosDbContext())
            {
                db.CashDrawerShifts.Add(new LocalCashDrawerShift
                {
                    ShiftId = -1,
                    ClientShiftId = "unconfirmed-1",
                    BranchId = 1,
                    TerminalId = 1,
                    CashierId = "cashier1",
                    OpeningFloat = 1000m,
                    ExpectedBalance = 1000m,
                    OpeningConfirmed = false,
                    Status = "OPEN",
                    OpenedAt = BusinessDayClock.UtcNow().AddMinutes(-5)
                });
                await db.SaveChangesAsync();
            }

            var session = CreateOfflineTestSession("OFFLINE-SESSION-TOKEN", "cashier1");
            using var offlineHttp = new HttpClient(new InProcSyncHandler(_ => throw new HttpRequestException("Offline"))) { BaseAddress = new Uri("http://localhost/") };

            var unconfirmed = await ShiftSession.VerifyPaymentShiftLocalFirstAsync(offlineHttp, session, new PosConnectivityService(offlineHttp));
            Assert(!unconfirmed.IsReady && unconfirmed.Issue == ShiftVerificationIssue.OpeningNotConfirmed,
                $"Unconfirmed open shift must classify as OpeningNotConfirmed (got {unconfirmed.Issue}).");

            using var checkoutDb = new LocalPosDbContext();
            var saleBlocked = await new OfflineCheckoutService(checkoutDb, session).ProcessSaleAsync(
                BuildSingleCashOrderDto(-1, "unconfirmed-1", "cashier1"), 224m, "CASH", null, "cashier1");
            Assert(!saleBlocked.Success, "OfflineCheckoutService must fail closed when shift opening is unconfirmed.");
        }

        // 5. ClosedShift (current business day) vs 6. Locked (4 AM Business Day Lock)
        using (var s4 = new TempLocalDbScope("off05_4"))
        {
            SetDeterministicManilaClock(dayUtc); // 14:00 Manila (2026-11-14)
            await SeedOfflineBaselineAsync(configureOpeningFund: true, openingFund: 1000m);
            using (var db = new LocalPosDbContext())
            {
                db.CashDrawerShifts.Add(new LocalCashDrawerShift
                {
                    ShiftId = 10,
                    ClientShiftId = "closed-today-1",
                    BranchId = 1,
                    TerminalId = 1,
                    CashierId = "cashier1",
                    OpeningFloat = 1000m,
                    ExpectedBalance = 1000m,
                    ActualCountedBalance = 1000m,
                    OpeningConfirmed = true,
                    Status = "CLOSED",
                    OpenedAt = dayUtc.AddHours(-4),
                    ClosedAt = dayUtc.AddHours(-1) // closed earlier in same business day, no terminal lock flags yet
                });
                await db.SaveChangesAsync();
            }

            var session = CreateOfflineTestSession("OFFLINE-SESSION-TOKEN", "cashier1");
            using var offlineHttp = new HttpClient(new InProcSyncHandler(_ => throw new HttpRequestException("Offline"))) { BaseAddress = new Uri("http://localhost/") };

            var closedRes = await ShiftSession.VerifyPaymentShiftLocalFirstAsync(offlineHttp, session, new PosConnectivityService(offlineHttp));
            Assert(!closedRes.IsReady && closedRes.Issue == ShiftVerificationIssue.ClosedShift,
                $"Same-day closed shift without terminal lock flags must classify as ClosedShift (got {closedRes.Issue}).");

            // Now apply 4 AM Business Day Lock on LocalTerminalConfig -> Locked until next day 4:00 AM Manila
            using (var lockDb = new LocalPosDbContext())
            {
                var term = await lockDb.TerminalConfigs.SingleAsync();
                ShiftSession.ApplyLocalClosingLock(term, "cashier1", dayUtc.AddHours(-1));
                await lockDb.SaveChangesAsync();
            }

            var lockedRes = await ShiftSession.VerifyPaymentShiftLocalFirstAsync(offlineHttp, session, new PosConnectivityService(offlineHttp));
            Assert(!lockedRes.IsReady && lockedRes.Issue == ShiftVerificationIssue.Locked,
                $"Before 4:00 AM Manila unlock boundary, VerifyPaymentShiftLocalFirstAsync must return Locked (got {lockedRes.Issue}).");

            // Advance past next 4:00 AM Manila (2026-11-14T20:05:00Z = 2026-11-15 04:05 Manila) -> lock releases to NoActiveShift
            SetDeterministicManilaClock(new DateTime(2026, 11, 14, 20, 5, 0, DateTimeKind.Utc), tickCount64: 50_000L + (long)TimeSpan.FromHours(15).TotalMilliseconds);
            var unlockedNextDay = await ShiftSession.VerifyPaymentShiftLocalFirstAsync(offlineHttp, session, new PosConnectivityService(offlineHttp));
            Assert(!unlockedNextDay.IsReady && unlockedNextDay.Issue == ShiftVerificationIssue.NoActiveShift,
                $"After 4:00 AM Manila boundary, lock must clear and allow new day opening (got {unlockedNextDay.Issue}).");
        }

        // 7. DifferentCashierOpen & 8. BranchMismatch / OutletMismatch
        using (var s5 = new TempLocalDbScope("off05_5"))
        {
            SetDeterministicManilaClock(dayUtc);
            await SeedOfflineBaselineAsync(configureOpeningFund: true, openingFund: 1000m);
            using (var db = new LocalPosDbContext())
            {
                db.CashDrawerShifts.Add(new LocalCashDrawerShift
                {
                    ShiftId = -1,
                    ClientShiftId = "shift-cashier1",
                    BranchId = 1,
                    TerminalId = 1,
                    CashierId = "cashier1",
                    OpeningFloat = 1000m,
                    ExpectedBalance = 1000m,
                    OpeningConfirmed = true,
                    Status = "OPEN",
                    OpenedAt = dayUtc.AddHours(-1)
                });
                await db.SaveChangesAsync();
            }

            using var offlineHttp = new HttpClient(new InProcSyncHandler(_ => throw new HttpRequestException("Offline"))) { BaseAddress = new Uri("http://localhost/") };

            var otherCashierSession = CreateOfflineTestSession("OFFLINE-SESSION-TOKEN", "cashier2", "Cashier", terminalId: 1, outletId: 1, branchId: 1);
            var diffCashier = await ShiftSession.VerifyPaymentShiftLocalFirstAsync(offlineHttp, otherCashierSession, new PosConnectivityService(offlineHttp));
            Assert(!diffCashier.IsReady && diffCashier.Issue == ShiftVerificationIssue.DifferentCashierOpen,
                $"Shift owned by cashier1 accessed by cashier2 must return DifferentCashierOpen (got {diffCashier.Issue}).");

            using (var checkoutDb = new LocalPosDbContext())
            {
                var wrongCashierSale = await new OfflineCheckoutService(checkoutDb, otherCashierSession).ProcessSaleAsync(
                    BuildSingleCashOrderDto(-1, "shift-cashier1", "cashier2"), 224m, "CASH", null, "cashier2");
                Assert(!wrongCashierSale.Success, "OfflineCheckoutService must fail closed for different cashier.");

                var wrongOutletSession = CreateOfflineTestSession("OFFLINE-SESSION-TOKEN", "cashier1", "Cashier", terminalId: 1, outletId: 99, branchId: 1);
                var wrongOutletVerify = await ShiftSession.VerifyPaymentShiftLocalFirstAsync(offlineHttp, wrongOutletSession, new PosConnectivityService(offlineHttp));
                Assert(!wrongOutletVerify.IsReady && wrongOutletVerify.Issue == ShiftVerificationIssue.BranchMismatch,
                    $"Outlet mismatch against local terminal config must return BranchMismatch (got {wrongOutletVerify.Issue}).");

                var wrongBranchSession = CreateOfflineTestSession("OFFLINE-SESSION-TOKEN", "cashier1", "Cashier", terminalId: 1, outletId: 1, branchId: 99);
                var wrongBranchVerify = await ShiftSession.VerifyPaymentShiftLocalFirstAsync(offlineHttp, wrongBranchSession, new PosConnectivityService(offlineHttp));
                Assert(!wrongBranchVerify.IsReady && wrongBranchVerify.Issue == ShiftVerificationIssue.BranchMismatch,
                    $"Branch mismatch must return BranchMismatch (got {wrongBranchVerify.Issue}).");

                var wrongBranchSale = await new OfflineCheckoutService(checkoutDb, wrongBranchSession).ProcessSaleAsync(
                    BuildSingleCashOrderDto(-1, "shift-cashier1", "cashier1", branchId: 99, outletId: 1), 224m, "CASH", null, "cashier1");
                Assert(!wrongBranchSale.Success, "OfflineCheckoutService must fail closed for mismatched branch/outlet.");
            }
        }

        // 9. Reachable 401/403/423 rejection MUST NOT bypass into offline sales even when local OPEN shift exists
        using (var s6 = new TempLocalDbScope("off05_6"))
        {
            SetDeterministicManilaClock(dayUtc);
            await SeedOfflineBaselineAsync(configureOpeningFund: true, openingFund: 1000m);
            using (var db = new LocalPosDbContext())
            {
                db.CashDrawerShifts.Add(new LocalCashDrawerShift
                {
                    ShiftId = 20,
                    ClientShiftId = "shift-online-valid",
                    BranchId = 1,
                    TerminalId = 1,
                    CashierId = "cashier1",
                    OpeningFloat = 1000m,
                    ExpectedBalance = 1000m,
                    OpeningConfirmed = true,
                    Status = "OPEN",
                    OpenedAt = dayUtc.AddHours(-1)
                });
                await db.SaveChangesAsync();
            }

            foreach (var (code, expectedIssue) in new[]
            {
                (HttpStatusCode.Unauthorized, ShiftVerificationIssue.AuthorizationRejected),
                (HttpStatusCode.Forbidden, ShiftVerificationIssue.AuthorizationRejected),
                (HttpStatusCode.Locked, ShiftVerificationIssue.Locked)
            })
            {
                // OFFLINE-SESSION-TOKEN causes EnsureOnlineBearerTokenWithStatusAsync to hit api/terminals/session-token when OnlineAndHealthy
                var session = CreateOfflineTestSession("OFFLINE-SESSION-TOKEN", "cashier1");
                using var rejectHttp = new HttpClient(new InProcSyncHandler(req =>
                {
                    string path = req.RequestUri!.AbsolutePath;
                    if (path.EndsWith("api/offline-sync/health", StringComparison.OrdinalIgnoreCase))
                    {
                        return Task.FromResult(JsonOk(new { Status = "ONLINE", Timestamp = BusinessDayClock.UtcNow() }));
                    }

                    if (path.EndsWith("api/terminals/session-token", StringComparison.OrdinalIgnoreCase))
                    {
                        return Task.FromResult(new HttpResponseMessage(code)
                        {
                            Content = new StringContent($"Rejected {(int)code}")
                        });
                    }

                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
                }))
                {
                    BaseAddress = new Uri("http://localhost/")
                };

                var rejectRes = await ShiftSession.VerifyPaymentShiftLocalFirstAsync(rejectHttp, session, new PosConnectivityService(rejectHttp));
                Assert(!rejectRes.IsReady && !rejectRes.Offline && rejectRes.Issue == expectedIssue,
                    $"Reachable HTTP {(int)code} must fail closed cleanly with {expectedIssue} and NEVER bypass to offline (actual: Ready={rejectRes.IsReady}, Offline={rejectRes.Offline}, Issue={rejectRes.Issue}).");
            }
        }

        // 10. Duplicate OPEN rows resolution (same ClientShiftId prefers positive) vs conflict (distinct ClientShiftIds fail closed)
        using (var s7 = new TempLocalDbScope("off05_7"))
        {
            SetDeterministicManilaClock(dayUtc);
            await SeedOfflineBaselineAsync(configureOpeningFund: true, openingFund: 1000m);

            using (var db = new LocalPosDbContext())
            {
                db.CashDrawerShifts.AddRange(
                    new LocalCashDrawerShift
                    {
                        ShiftId = -10,
                        ClientShiftId = "dup-same-client",
                        BranchId = 1,
                        TerminalId = 1,
                        CashierId = "cashier1",
                        OpeningFloat = 1000m,
                        ExpectedBalance = 1000m,
                        OpeningConfirmed = true,
                        Status = "OPEN",
                        OpenedAt = dayUtc.AddMinutes(-20)
                    },
                    new LocalCashDrawerShift
                    {
                        ShiftId = 55,
                        ClientShiftId = "dup-same-client",
                        BranchId = 1,
                        TerminalId = 1,
                        CashierId = "cashier1",
                        OpeningFloat = 1000m,
                        ExpectedBalance = 1000m,
                        OpeningConfirmed = true,
                        Status = "OPEN",
                        OpenedAt = dayUtc.AddMinutes(-20)
                    });
                await db.SaveChangesAsync();

                var resolved = await ShiftSession.GetLocalActiveOpenShiftAsync(db, 1, asNoTracking: true);
                Assert(resolved != null && resolved.ShiftId == 55,
                    $"Duplicate OPEN rows with same ClientShiftId must resolve cleanly to positive ShiftId 55 (got {resolved?.ShiftId}).");
            }

            // Add conflicting distinct ClientShiftId OPEN row -> GetLocalActiveOpenShiftAsync throws & VerifyPaymentShiftLocalFirstAsync returns LocalDatabaseError
            using (var conflictDb = new LocalPosDbContext())
            {
                conflictDb.CashDrawerShifts.Add(new LocalCashDrawerShift
                {
                    ShiftId = -11,
                    ClientShiftId = "conflict-distinct-client",
                    BranchId = 1,
                    TerminalId = 1,
                    CashierId = "cashier1",
                    OpeningFloat = 1000m,
                    ExpectedBalance = 1000m,
                    OpeningConfirmed = true,
                    Status = "OPEN",
                    OpenedAt = dayUtc.AddMinutes(-10)
                });
                await conflictDb.SaveChangesAsync();

                bool threwInvalidOp = false;
                try
                {
                    await ShiftSession.GetLocalActiveOpenShiftAsync(conflictDb, 1, asNoTracking: true);
                }
                catch (InvalidOperationException ex) when (ex.Message.Contains("Multiple conflicting open shifts", StringComparison.OrdinalIgnoreCase))
                {
                    threwInvalidOp = true;
                }
                Assert(threwInvalidOp, "Distinct OPEN ClientShiftIds must throw InvalidOperationException rather than picking arbitrarily.");
            }

            var session = CreateOfflineTestSession("OFFLINE-SESSION-TOKEN", "cashier1");
            using var offlineHttp = new HttpClient(new InProcSyncHandler(_ => throw new HttpRequestException("Offline"))) { BaseAddress = new Uri("http://localhost/") };
            var conflictVerify = await ShiftSession.VerifyPaymentShiftLocalFirstAsync(offlineHttp, session, new PosConnectivityService(offlineHttp));
            Assert(!conflictVerify.IsReady && conflictVerify.Issue == ShiftVerificationIssue.LocalDatabaseError,
                $"Conflicting OPEN shifts must fail closed with LocalDatabaseError (got {conflictVerify.Issue}).");
        }

        // 11. Expired terminal license check (ClientLicenseService blocks operations & OfflineCheckoutService)
        using (var s8 = new TempLocalDbScope("off05_8"))
        {
            SetDeterministicManilaClock(dayUtc);
            ClientLicenseService.StrictEnforcementMode = true;
            DeviceBindingHelper.HardwareFingerprintProvider = () => "HW-OFFLINE-05";

            var (conn, serverDb, serverShift) = await CreateTestEnvAsync();
            await using var cleanup = conn;

            var term = await serverDb.Terminals.FindAsync(1);
            term!.Uuid = "uuid-term-001";
            term.IsActivationCodeUsed = true;
            await serverDb.SaveChangesAsync();

            var licSvc = new LicensingService(serverDb);
            var pkg = await licSvc.EnsureOrActivateTerminalLicenseAsync(term, "uuid-term-001", null, "Admin");

            using (var seedLocal = new LocalPosDbContext())
            {
                await SeedLocalSqliteForLicensingTestAsync(seedLocal, 1, 1, 1, "uuid-term-001", serverShift.ShiftId);
            }

            var clientLic = new ClientLicenseService();
            await clientLic.SaveSignedLicensePackageAsync(pkg!);

            // Advance past 5-year expiration (+1 day)
            SetDeterministicManilaClock(dayUtc.AddYears(5).AddDays(1));
            var gate = await ClientLicenseService.CheckOperationalGateAsync(1);
            Assert(!gate.Allowed && (gate.ErrorMessage ?? string.Empty).Contains("expired", StringComparison.OrdinalIgnoreCase),
                $"Expired license must fail ClientLicenseService.CheckOperationalGateAsync (Allowed={gate.Allowed}, Error={gate.ErrorMessage}).");

            var session = CreateOfflineTestSession("OFFLINE-SESSION-TOKEN", "cashier1");
            using var coDb = new LocalPosDbContext();
            var blockedSale = await new OfflineCheckoutService(coDb, session).ProcessSaleAsync(
                BuildSingleCashOrderDto(serverShift.ShiftId, cashierId: "cashier1"), 500m, "CASH", null, "cashier1");
            Assert(!blockedSale.Success && (blockedSale.ErrorMessage ?? string.Empty).Contains("expired", StringComparison.OrdinalIgnoreCase),
                $"Expired license must block OfflineCheckoutService.ProcessSaleAsync (got Success={blockedSale.Success}, Err={blockedSale.ErrorMessage}).");
        }
    }
}

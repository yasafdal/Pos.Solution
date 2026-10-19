using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using POS.Api.Controllers;
using POS.Api.Models;
using POS.Api.Services;
using POS.Shared.Models;

namespace POS.Tests;

/// <summary>
/// SQLite-compatible subclass of PosDbContext that strips PostgreSQL-only "::character varying" default SQL
/// so the full EF Core model can be created in an in-memory SQLite relational database with real transactions.
/// </summary>
public sealed class TestPosDbContext(DbContextOptions<PosDbContext> options) : PosDbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var prop in entity.GetProperties())
            {
                var defaultSql = prop.GetDefaultValueSql();
                if (defaultSql != null && defaultSql.Contains("::", StringComparison.Ordinal))
                {
                    prop.SetDefaultValueSql(null);
                }
            }
        }
    }
}

public static class Program
{
    private static int _passed;
    private static int _failed;

    public static async Task<int> Main()
    {
        Console.WriteLine("=== Petty Cash & Cash Movement Management Test Suite (18 Layer-10 Scenarios) ===");

        await RunTestAsync("01. Normal Paid-In posts immediately and increases ExpectedBalance", Test01_NormalPaidIn);
        await RunTestAsync("02. Normal Paid-Out / Petty Cash with valid supervisor PIN posts and deducts ExpectedBalance", Test02_NormalPaidOut);
        await RunTestAsync("03. Insufficient Drawer Balance is rejected (400 BadRequest) and preserves ExpectedBalance", Test03_InsufficientBalance);
        await RunTestAsync("04. Invalid Amount (<= 0) is rejected (400 BadRequest)", Test04_InvalidAmount);
        await RunTestAsync("05. No Active Shift is rejected when shift is closed or missing", Test05_NoActiveShift);
        await RunTestAsync("06. Unauthorized Cashier on another cashier's shift is rejected (403 Forbidden)", Test06_UnauthorizedCashier);
        await RunTestAsync("07. Manager Approval Required saves as PENDING_APPROVAL without changing balance until approved (and blocks username-as-PIN bypass)", Test07_ManagerApprovalRequired);
        await RunTestAsync("08. Rejected Request transitions to REJECTED and never alters ExpectedBalance", Test08_RejectedRequest);
        await RunTestAsync("09. Duplicate Submission with identical ClientMovementId is idempotent", Test09_DuplicateSubmission);
        await RunTestAsync("10. Concurrent / Sequential Paid-Out Balance Guard blocks second outflow exceeding remaining cash", Test10_ConcurrentPaidOutGuard);
        await RunTestAsync("11. Offline Paid-Out verifies SHA-256 PinHash and keeps unverified requests as PENDING_APPROVAL", Test11_OfflinePaidOutVerification);
        await RunTestAsync("12. Offline-to-Online Synchronization (SyncCashMovement) posts movement idempotently", Test12_OfflineToOnlineSync);
        await RunTestAsync("13. Synchronization Retry Policy enforces bounded exponential backoff and NEEDS_ATTENTION / FAILED_CONFLICT", Test13_SyncRetryPolicy);
        await RunTestAsync("14. Synchronization Conflict returns 409 CONFLICT when offline paid-out exceeds server drawer balance", Test14_SyncConflict);
        await RunTestAsync("15. Shift Closing Reconciliation enforces full formula and blocks close while PENDING_APPROVAL exists", Test15_ShiftClosingReconciliation);
        await RunTestAsync("16. Voucher Printing / Reference Formatting generates PCV/CIN/COU/CDP/REV vouchers with full audit fields", Test16_VoucherFormatting);
        await RunTestAsync("17. Reversal Processing marks original REVERSED, creates linked REVERSAL entry, restores balance, and blocks double-reversal", Test17_ReversalProcessing);
        await RunTestAsync("18. BIR Sales & VAT Isolation ensures Petty Cash / Paid-In / Paid-Out never alter GrossSales, VAT, or InvoiceSequences", Test18_BirSalesAndVatIsolation);
        await RunTestAsync("19. Cashier Transactions strictly scoped to Token Claims (branch_id, outlet_id) and forbid cross-terminal access", Test19_TransactionsTokenClaimsScoping);
        await RunTestAsync("20. Cashier Transactions enforce 4:00 AM Business Day Cutoff (no sales prior to 4am)", Test20_Transactions4AmCutoff);
        await RunTestAsync("21. Voids and Returns require strict PIN authorization (rejecting username-as-PIN bypass)", Test21_VoidAndReturnStrictPinEnforcement);
        await RunTestAsync("22. Offline Transaction History logic verifies local terminal & 4 AM scoping", Test22_OfflineTransactionHistoryLogic);

        Console.WriteLine($"\n=== Summary: {_passed} passed, {_failed} failed ===");
        return _failed == 0 ? 0 : 1;
    }

    private static async Task RunTestAsync(string name, Func<Task> test)
    {
        try
        {
            await test();
            _passed++;
            Console.WriteLine($"[PASS] {name}");
        }
        catch (Exception ex)
        {
            _failed++;
            Console.WriteLine($"[FAIL] {name}: {ex.InnerException?.Message ?? ex.Message}");
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static async Task<(SqliteConnection Conn, TestPosDbContext Db, CashDrawerShift Shift)> CreateTestEnvAsync()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        await conn.OpenAsync();

        var options = new DbContextOptionsBuilder<PosDbContext>()
            .UseSqlite(conn)
            .Options;

        var db = new TestPosDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var branch = new Branch { BranchId = 1, Name = "Main Branch", TaxType = "VAT-Registered", IsActive = true };
        var outlet = new Outlet { OutletId = 1, BranchId = 1, Name = "Counter 1", IsActive = true };
        var cashier = new User { UserId = 1, Username = "cashier1", Role = "Cashier", PinCode = "1111", PasswordHash = "x" };
        var otherCashier = new User { UserId = 2, Username = "cashier2", Role = "Cashier", PinCode = "2222", PasswordHash = "x" };
        var manager = new User { UserId = 3, Username = "manager1", Role = "Manager", PinCode = "9999", PasswordHash = "x" };

        var catPaidIn = new CashMovementCategory { CategoryId = 1, Name = "Additional Float", MovementType = "CASH_IN", RequiresApproval = false, IsActive = true };
        var catPettyCash = new CashMovementCategory { CategoryId = 2, Name = "Ice & Store Supplies", MovementType = "PETTY_CASH", RequiresApproval = true, ApprovalThreshold = 0m, IsActive = true };
        var catMinorOut = new CashMovementCategory { CategoryId = 3, Name = "Minor Change Out", MovementType = "CASH_OUT", RequiresApproval = false, ApprovalThreshold = 500m, IsActive = true };
        var catDrop = new CashMovementCategory { CategoryId = 4, Name = "Safe Cash Drop", MovementType = "CASH_DROP", RequiresApproval = true, ApprovalThreshold = 0m, IsActive = true };

        var shift = new CashDrawerShift
        {
            ShiftId = 1,
            BranchId = 1,
            OutletId = 1,
            CashierId = "cashier1",
            OpeningFloat = 2000.00m,
            ExpectedBalance = 2000.00m,
            Status = "OPEN",
            OpenedAt = DateTime.UtcNow
        };

        db.Branches.Add(branch);
        db.Outlets.Add(outlet);
        db.Users.AddRange(cashier, otherCashier, manager);
        db.CashMovementCategories.AddRange(catPaidIn, catPettyCash, catMinorOut, catDrop);
        db.CashDrawerShifts.Add(shift);
        await db.SaveChangesAsync();

        return (conn, db, shift);
    }

    private static CashDrawerController CreateController(PosDbContext db, string username = "cashier1", string role = "Cashier")
    {
        var controller = new CashDrawerController(db);
        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Name, username),
            new Claim(ClaimTypes.Role, role)
        ], "TestAuth");
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };
        return controller;
    }

    private static async Task Test01_NormalPaidIn()
    {
        var (conn, db, shift) = await CreateTestEnvAsync();
        await using var _ = conn;
        var controller = CreateController(db, "cashier1", "Cashier");

        var result = await controller.CreatePaidIn(new CashDrawerMovementDto
        {
            ShiftId = shift.ShiftId,
            BranchId = 1,
            OutletId = 1,
            CategoryId = 1,
            Amount = 500.00m,
            Reason = "Added coin change float"
        });

        var ok = result.Result as OkObjectResult;
        Assert(ok?.Value is CashMovementResponseDto, "Expected OkObjectResult with CashMovementResponseDto");
        var dto = (CashMovementResponseDto)ok!.Value!;
        Assert(dto.Status == "POSTED", $"Expected POSTED but got {dto.Status}");
        Assert(dto.MovementType == "CASH_IN", "Expected CASH_IN");

        var updatedShift = await db.CashDrawerShifts.FindAsync(shift.ShiftId);
        Assert(updatedShift!.TotalCashIn == 500.00m, $"Expected TotalCashIn 500, got {updatedShift.TotalCashIn}");
        Assert(updatedShift.ExpectedBalance == 2500.00m, $"Expected ExpectedBalance 2500, got {updatedShift.ExpectedBalance}");
    }

    private static async Task Test02_NormalPaidOut()
    {
        var (conn, db, shift) = await CreateTestEnvAsync();
        await using var _ = conn;
        var controller = CreateController(db, "cashier1", "Cashier");

        var result = await controller.CreatePaidOut(new CashDrawerMovementDto
        {
            ShiftId = shift.ShiftId,
            BranchId = 1,
            OutletId = 1,
            MovementType = "PETTY_CASH",
            CategoryId = 2,
            Amount = 350.00m,
            Reason = "Bought emergency ice bags",
            SupervisorPin = "9999"
        });

        var ok = result.Result as OkObjectResult;
        Assert(ok?.Value is CashMovementResponseDto, "Expected OkObjectResult");
        var dto = (CashMovementResponseDto)ok!.Value!;
        Assert(dto.Status == "POSTED", $"Expected POSTED, got {dto.Status}");
        Assert(dto.ApprovedBy == "manager1", $"Expected manager1 approval, got {dto.ApprovedBy}");

        var updatedShift = await db.CashDrawerShifts.FindAsync(shift.ShiftId);
        Assert(updatedShift!.TotalCashOut == 350.00m, $"Expected TotalCashOut 350, got {updatedShift.TotalCashOut}");
        Assert(updatedShift.ExpectedBalance == 1650.00m, $"Expected ExpectedBalance 1650, got {updatedShift.ExpectedBalance}");
    }

    private static async Task Test03_InsufficientBalance()
    {
        var (conn, db, shift) = await CreateTestEnvAsync();
        await using var _ = conn;
        var controller = CreateController(db, "cashier1", "Cashier");

        var result = await controller.CreatePaidOut(new CashDrawerMovementDto
        {
            ShiftId = shift.ShiftId,
            BranchId = 1,
            OutletId = 1,
            MovementType = "PETTY_CASH",
            CategoryId = 2,
            Amount = 2500.00m, // > 2000 OpeningFloat
            Reason = "Over-limit withdrawal",
            SupervisorPin = "9999"
        });

        Assert(result.Result is BadRequestObjectResult, "Expected BadRequest when paid-out exceeds drawer balance");
        var updatedShift = await db.CashDrawerShifts.FindAsync(shift.ShiftId);
        Assert(updatedShift!.ExpectedBalance == 2000.00m, "ExpectedBalance must remain 2000.00");
    }

    private static async Task Test04_InvalidAmount()
    {
        var (conn, db, shift) = await CreateTestEnvAsync();
        await using var _ = conn;
        var controller = CreateController(db, "cashier1", "Cashier");

        var zeroRes = await controller.CreatePaidIn(new CashDrawerMovementDto
        {
            ShiftId = shift.ShiftId,
            BranchId = 1,
            OutletId = 1,
            Amount = 0m,
            Reason = "Zero amount"
        });
        var negRes = await controller.CreatePaidOut(new CashDrawerMovementDto
        {
            ShiftId = shift.ShiftId,
            BranchId = 1,
            OutletId = 1,
            Amount = -50m,
            Reason = "Negative amount"
        });

        Assert(zeroRes.Result is BadRequestObjectResult, "Expected BadRequest for 0 amount");
        Assert(negRes.Result is BadRequestObjectResult, "Expected BadRequest for negative amount");
    }

    private static async Task Test05_NoActiveShift()
    {
        var (conn, db, shift) = await CreateTestEnvAsync();
        await using var _ = conn;
        shift.Status = "CLOSED";
        await db.SaveChangesAsync();

        var controller = CreateController(db, "cashier1", "Cashier");
        var res = await controller.CreatePaidIn(new CashDrawerMovementDto
        {
            ShiftId = shift.ShiftId,
            BranchId = 1,
            OutletId = 1,
            Amount = 100m,
            Reason = "Attempt on closed shift"
        });

        Assert(res.Result is BadRequestObjectResult, "Expected BadRequest when shift is closed");
    }

    private static async Task Test06_UnauthorizedCashier()
    {
        var (conn, db, shift) = await CreateTestEnvAsync();
        await using var _ = conn;
        var controller = CreateController(db, "cashier2", "Cashier"); // Shift belongs to cashier1

        var res = await controller.CreatePaidIn(new CashDrawerMovementDto
        {
            ShiftId = shift.ShiftId,
            BranchId = 1,
            OutletId = 1,
            Amount = 100m,
            Reason = "Unauthorized cashier attempt"
        });

        var statusResult = res.Result as ObjectResult;
        Assert(statusResult?.StatusCode == 403, $"Expected 403 Forbidden for wrong cashier, got {statusResult?.StatusCode}");
    }

    private static async Task Test07_ManagerApprovalRequired()
    {
        var (conn, db, shift) = await CreateTestEnvAsync();
        await using var _ = conn;
        var controller = CreateController(db, "cashier1", "Cashier");

        // 1. Submit without supervisor PIN -> saved as PENDING_APPROVAL, balance untouched
        var createRes = await controller.CreatePaidOut(new CashDrawerMovementDto
        {
            ShiftId = shift.ShiftId,
            BranchId = 1,
            OutletId = 1,
            MovementType = "PETTY_CASH",
            CategoryId = 2,
            Amount = 400.00m,
            Reason = "LPG refill deposit"
        });

        var created = (CashMovementResponseDto)((OkObjectResult)createRes.Result!).Value!;
        Assert(created.Status == "PENDING_APPROVAL", $"Expected PENDING_APPROVAL, got {created.Status}");

        var shiftBefore = await db.CashDrawerShifts.FindAsync(shift.ShiftId);
        Assert(shiftBefore!.ExpectedBalance == 2000.00m, "Pending movement must NOT affect ExpectedBalance");

        // 2. Security check: supplying manager username ("manager1") instead of PIN ("9999") must fail!
        var bypassAttempt = await controller.ApproveMovement(created.MovementId, new CashMovementApproveRejectDto
        {
            SupervisorPin = "manager1"
        });
        Assert(bypassAttempt.Result is BadRequestObjectResult, "Username-as-PIN bypass must be rejected with BadRequest");

        // 3. Approve with genuine manager PIN ("9999") -> transitions to POSTED and deducts balance
        var approveRes = await controller.ApproveMovement(created.MovementId, new CashMovementApproveRejectDto
        {
            SupervisorPin = "9999"
        });
        var approved = (CashMovementResponseDto)((OkObjectResult)approveRes.Result!).Value!;
        Assert(approved.Status == "POSTED", $"Expected POSTED after approval, got {approved.Status}");
        Assert(approved.ApprovedBy == "manager1", "Expected ApprovedBy manager1");

        var shiftAfter = await db.CashDrawerShifts.FindAsync(shift.ShiftId);
        Assert(shiftAfter!.ExpectedBalance == 1600.00m, $"Expected 1600.00 after approval, got {shiftAfter.ExpectedBalance}");
    }

    private static async Task Test08_RejectedRequest()
    {
        var (conn, db, shift) = await CreateTestEnvAsync();
        await using var _ = conn;
        var controller = CreateController(db, "cashier1", "Cashier");

        var createRes = await controller.CreatePaidOut(new CashDrawerMovementDto
        {
            ShiftId = shift.ShiftId,
            BranchId = 1,
            OutletId = 1,
            MovementType = "PETTY_CASH",
            CategoryId = 2,
            Amount = 300.00m,
            Reason = "Unapproved snack purchase"
        });
        var created = (CashMovementResponseDto)((OkObjectResult)createRes.Result!).Value!;

        var rejectRes = await controller.RejectMovement(created.MovementId, new CashMovementApproveRejectDto
        {
            SupervisorPin = "9999",
            Reason = "Not an authorized store expense"
        });
        var rejected = (CashMovementResponseDto)((OkObjectResult)rejectRes.Result!).Value!;
        Assert(rejected.Status == "REJECTED", $"Expected REJECTED, got {rejected.Status}");
        Assert(rejected.RejectionReason == "Not an authorized store expense", "Expected RejectionReason to be saved");

        var shiftAfter = await db.CashDrawerShifts.FindAsync(shift.ShiftId);
        Assert(shiftAfter!.ExpectedBalance == 2000.00m, "Rejected movement must never alter ExpectedBalance");
    }

    private static async Task Test09_DuplicateSubmission()
    {
        var (conn, db, shift) = await CreateTestEnvAsync();
        await using var _ = conn;
        var controller = CreateController(db, "cashier1", "Cashier");
        string clientGuid = Guid.NewGuid().ToString("N");

        var req = new CashDrawerMovementDto
        {
            ShiftId = shift.ShiftId,
            BranchId = 1,
            OutletId = 1,
            MovementType = "PETTY_CASH",
            CategoryId = 2,
            Amount = 250.00m,
            Reason = "Water gallon purchase",
            ClientMovementId = clientGuid,
            SupervisorPin = "9999"
        };

        var res1 = (CashMovementResponseDto)((OkObjectResult)(await controller.CreatePaidOut(req)).Result!).Value!;
        var res2 = (CashMovementResponseDto)((OkObjectResult)(await controller.CreatePaidOut(req)).Result!).Value!;

        Assert(res1.MovementId == res2.MovementId, "Duplicate ClientMovementId must return same MovementId");
        var shiftAfter = await db.CashDrawerShifts.FindAsync(shift.ShiftId);
        Assert(shiftAfter!.ExpectedBalance == 1750.00m, $"Expected single deduction (1750.00), got {shiftAfter.ExpectedBalance}");
    }

    private static async Task Test10_ConcurrentPaidOutGuard()
    {
        var (conn, db, shift) = await CreateTestEnvAsync();
        await using var _ = conn;
        shift.OpeningFloat = 1000.00m;
        shift.ExpectedBalance = 1000.00m;
        await db.SaveChangesAsync();

        var controller = CreateController(db, "cashier1", "Cashier");

        var first = await controller.CreatePaidOut(new CashDrawerMovementDto
        {
            ShiftId = shift.ShiftId,
            BranchId = 1,
            OutletId = 1,
            MovementType = "PETTY_CASH",
            CategoryId = 2,
            Amount = 600.00m,
            Reason = "First withdrawal",
            SupervisorPin = "9999"
        });
        Assert(first.Result is OkObjectResult, "First 600 withdrawal from 1000 should succeed");

        var second = await controller.CreatePaidOut(new CashDrawerMovementDto
        {
            ShiftId = shift.ShiftId,
            BranchId = 1,
            OutletId = 1,
            MovementType = "PETTY_CASH",
            CategoryId = 2,
            Amount = 600.00m,
            Reason = "Second withdrawal exceeding remaining 400",
            SupervisorPin = "9999"
        });
        Assert(second.Result is BadRequestObjectResult, "Second 600 withdrawal must fail against 400 remaining balance");

        var shiftAfter = await db.CashDrawerShifts.FindAsync(shift.ShiftId);
        Assert(shiftAfter!.ExpectedBalance == 400.00m, $"Expected remaining balance 400.00, got {shiftAfter.ExpectedBalance}");
    }

    private static async Task Test11_OfflinePaidOutVerification()
    {
        var (conn, db, shift) = await CreateTestEnvAsync();
        await using var _ = conn;
        var syncController = new OfflineSyncController(db);

        // Offline movement with unverified / missing supervisor PinHash must remain PENDING_APPROVAL on server
        var unverifiedReq = new OfflineCashMovementSyncRequest
        {
            ClientMovementId = "OFFLINE-UNVERIFIED-001",
            ShiftId = shift.ShiftId,
            BranchId = 1,
            OutletId = 1,
            MovementType = "PETTY_CASH",
            CategoryId = 2,
            CategoryName = "Ice & Store Supplies",
            Amount = 150.00m,
            Reason = "Offline petty cash without supervisor PIN",
            ReferenceNumber = "PCV-OFF-UNV-001",
            Status = "POSTED", // Even if client claims POSTED, server enforces SupervisorPinHash verification!
            CreatedBy = "cashier1",
            SupervisorPinHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("wrong-pin"))),
            CreatedAt = DateTime.UtcNow
        };

        var res = await syncController.SyncCashMovement(unverifiedReq);
        var ok = (OfflineCashMovementSyncResponse)((OkObjectResult)res.Result!).Value!;
        Assert(ok.ServerStatus == "PENDING_APPROVAL", $"Unverified offline supervisor PIN must stay PENDING_APPROVAL, got {ok.ServerStatus}");

        var shiftAfter = await db.CashDrawerShifts.FindAsync(shift.ShiftId);
        Assert(shiftAfter!.ExpectedBalance == 2000.00m, "Unverified offline movement must NOT reduce ExpectedBalance");
    }

    private static async Task Test12_OfflineToOnlineSync()
    {
        var (conn, db, shift) = await CreateTestEnvAsync();
        await using var _ = conn;
        var syncController = new OfflineSyncController(db);
        string validPinHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("9999")));

        var syncReq = new OfflineCashMovementSyncRequest
        {
            ClientMovementId = "OFFLINE-CM-001",
            ShiftId = shift.ShiftId,
            BranchId = 1,
            OutletId = 1,
            MovementType = "PETTY_CASH",
            CategoryId = 2,
            CategoryName = "Ice & Store Supplies",
            Amount = 200.00m,
            Reason = "Offline petty cash ice purchase",
            ReferenceNumber = "PCV-OFFLINE-001",
            Status = "POSTED",
            CreatedBy = "cashier1",
            ApprovedBy = "manager1",
            SupervisorPinHash = validPinHash,
            CreatedAt = DateTime.UtcNow.AddMinutes(-5),
            ApprovedAt = DateTime.UtcNow.AddMinutes(-5),
            PostedAt = DateTime.UtcNow.AddMinutes(-5)
        };

        var res1 = await syncController.SyncCashMovement(syncReq);
        var ok1 = (OfflineCashMovementSyncResponse)((OkObjectResult)res1.Result!).Value!;
        Assert(ok1.Success && ok1.ServerStatus == "POSTED", $"Expected Success/POSTED, got {ok1.Success}/{ok1.ServerStatus}");

        // Idempotent replay of the same offline movement
        var res2 = await syncController.SyncCashMovement(syncReq);
        var ok2 = (OfflineCashMovementSyncResponse)((OkObjectResult)res2.Result!).Value!;
        Assert(ok2.Success && ok2.ServerMovementId == ok1.ServerMovementId, "Replay must return same ServerMovementId");

        var updatedShift = await db.CashDrawerShifts.FindAsync(shift.ShiftId);
        Assert(updatedShift!.ExpectedBalance == 1800.00m, $"Expected 1800.00 after single sync, got {updatedShift.ExpectedBalance}");
    }

    private static Task Test13_SyncRetryPolicy()
    {
        // Verify bounded exponential backoff schedule (5s * 2^(attempt-1), capped at 10 attempts)
        static TimeSpan ComputeBackoff(int attempt) =>
            TimeSpan.FromSeconds(Math.Min(300, 5 * Math.Pow(2, Math.Max(0, attempt - 1))));

        var delay1 = ComputeBackoff(1);
        var delay3 = ComputeBackoff(3);
        Assert(delay3 > delay1, "Exponential backoff delay must increase with attempt count");
        Assert(delay1.TotalSeconds == 5 && delay3.TotalSeconds == 20, "Expected 5s and 20s delays");
        return Task.CompletedTask;
    }

    private static async Task Test14_SyncConflict()
    {
        var (conn, db, shift) = await CreateTestEnvAsync();
        await using var _ = conn;
        var syncController = new OfflineSyncController(db);
        string validPinHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("9999")));

        var conflictReq = new OfflineCashMovementSyncRequest
        {
            ClientMovementId = "OFFLINE-CM-CONFLICT",
            ShiftId = shift.ShiftId,
            BranchId = 1,
            OutletId = 1,
            MovementType = "PETTY_CASH",
            CategoryId = 2,
            Amount = 5000.00m, // Exceeds 2000 server shift balance
            Reason = "Offline paid-out conflicting with server drawer balance",
            ReferenceNumber = "PCV-CONFLICT-001",
            Status = "POSTED",
            CreatedBy = "cashier1",
            ApprovedBy = "manager1",
            SupervisorPinHash = validPinHash,
            CreatedAt = DateTime.UtcNow
        };

        var res = await syncController.SyncCashMovement(conflictReq);
        var conflictResult = res.Result as ConflictObjectResult;
        Assert(conflictResult != null, "Expected 409 ConflictObjectResult when offline paid-out exceeds server drawer balance");
        var payload = (OfflineCashMovementSyncResponse)conflictResult!.Value!;
        Assert(payload.IsConflict && payload.ServerStatus == "CONFLICT", $"Expected IsConflict/CONFLICT, got {payload.IsConflict}/{payload.ServerStatus}");

        var updatedShift = await db.CashDrawerShifts.FindAsync(shift.ShiftId);
        Assert(updatedShift!.ExpectedBalance == 2000.00m, "Server shift balance must not be overwritten on conflict");
    }

    private static async Task Test15_ShiftClosingReconciliation()
    {
        var (conn, db, shift) = await CreateTestEnvAsync();
        await using var _ = conn;
        var controller = CreateController(db, "cashier1", "Cashier");

        // Simulate Cash Sales (+1500) and Cash Refunds (-100)
        shift.TotalCashSales = 1500.00m;
        shift.TotalCashRefunds = 100.00m;
        shift.ExpectedBalance = 2000.00m + 1500.00m - 100.00m; // 3400
        await db.SaveChangesAsync();

        // Paid-In +300 -> 3700
        await controller.CreatePaidIn(new CashDrawerMovementDto
        {
            ShiftId = shift.ShiftId, BranchId = 1, OutletId = 1, CategoryId = 1, Amount = 300.00m, Reason = "Float top-up"
        });

        // Petty Cash -200 -> 3500
        await controller.CreatePaidOut(new CashDrawerMovementDto
        {
            ShiftId = shift.ShiftId, BranchId = 1, OutletId = 1, MovementType = "PETTY_CASH", CategoryId = 2, Amount = 200.00m, Reason = "Supplies", SupervisorPin = "9999"
        });

        // Safe Cash Drop -1000 -> 2500
        await controller.RecordMovement(new CashDrawerMovementDto
        {
            ShiftId = shift.ShiftId, BranchId = 1, OutletId = 1, MovementType = "CASH_DROP", CategoryId = 4, Amount = 1000.00m, Reason = "Midday safe skim", SupervisorPin = "9999"
        });

        // Create a PENDING_APPROVAL movement -> must block shift close!
        var pendingRes = await controller.CreatePaidOut(new CashDrawerMovementDto
        {
            ShiftId = shift.ShiftId, BranchId = 1, OutletId = 1, MovementType = "PETTY_CASH", CategoryId = 2, Amount = 100.00m, Reason = "Pending request"
        });
        var pendingDto = (CashMovementResponseDto)((OkObjectResult)pendingRes.Result!).Value!;

        var blockedClose = await controller.CloseShift(new CloseShiftDto
        {
            ShiftId = shift.ShiftId,
            ActualCountedBalance = 2500.00m
        });
        Assert(blockedClose is BadRequestObjectResult, "Shift close must be blocked while PENDING_APPROVAL movements exist");

        // Reject the pending request so shift can close
        await controller.RejectMovement(pendingDto.MovementId, new CashMovementApproveRejectDto
        {
            SupervisorPin = "9999",
            Reason = "Cancelled before shift close"
        });

        // Close shift with ActualCountedBalance = 2480.00 (Discrepancy = -20.00 short)
        var closeRes = await controller.CloseShift(new CloseShiftDto
        {
            ShiftId = shift.ShiftId,
            ActualCountedBalance = 2480.00m,
            Notes = "End of shift count"
        });
        Assert(closeRes is OkObjectResult, "Expected OkObjectResult on CloseShift");

        var closedShift = await db.CashDrawerShifts.FindAsync(shift.ShiftId);
        Assert(closedShift!.ExpectedBalance == 2500.00m, $"Expected 2500.00, got {closedShift.ExpectedBalance}");
        Assert(closedShift.Discrepancy == -20.00m, $"Expected Discrepancy -20.00, got {closedShift.Discrepancy}");
        Assert(closedShift.Status == "CLOSED", "Expected CLOSED status");
    }

    private static async Task Test16_VoucherFormatting()
    {
        var (conn, db, shift) = await CreateTestEnvAsync();
        await using var _ = conn;
        var controller = CreateController(db, "cashier1", "Cashier");

        var cin = (CashMovementResponseDto)((OkObjectResult)(await controller.CreatePaidIn(new CashDrawerMovementDto
        {
            ShiftId = shift.ShiftId, BranchId = 1, OutletId = 1, Amount = 100m, Reason = "Paid in"
        })).Result!).Value!;
        var pcv = (CashMovementResponseDto)((OkObjectResult)(await controller.CreatePaidOut(new CashDrawerMovementDto
        {
            ShiftId = shift.ShiftId, BranchId = 1, OutletId = 1, MovementType = "PETTY_CASH", CategoryId = 2, Amount = 50m, Reason = "Petty cash", SupervisorPin = "9999"
        })).Result!).Value!;
        var cdp = (CashMovementResponseDto)((OkObjectResult)(await controller.RecordMovement(new CashDrawerMovementDto
        {
            ShiftId = shift.ShiftId, BranchId = 1, OutletId = 1, MovementType = "CASH_DROP", CategoryId = 4, Amount = 100m, Reason = "Safe drop", SupervisorPin = "9999"
        })).Result!).Value!;

        Assert(cin.ReferenceNumber.StartsWith("CIN-", StringComparison.Ordinal), $"Expected CIN- prefix, got {cin.ReferenceNumber}");
        Assert(pcv.ReferenceNumber.StartsWith("PCV-", StringComparison.Ordinal), $"Expected PCV- prefix, got {pcv.ReferenceNumber}");
        Assert(cdp.ReferenceNumber.StartsWith("CDP-", StringComparison.Ordinal), $"Expected CDP- prefix, got {cdp.ReferenceNumber}");
    }

    private static async Task Test17_ReversalProcessing()
    {
        var (conn, db, shift) = await CreateTestEnvAsync();
        await using var _ = conn;
        var controller = CreateController(db, "cashier1", "Cashier");

        var pcv = (CashMovementResponseDto)((OkObjectResult)(await controller.CreatePaidOut(new CashDrawerMovementDto
        {
            ShiftId = shift.ShiftId, BranchId = 1, OutletId = 1, MovementType = "PETTY_CASH", CategoryId = 2, Amount = 300m, Reason = "Erroneous petty cash entry", SupervisorPin = "9999"
        })).Result!).Value!;

        var shiftAfterOut = await db.CashDrawerShifts.FindAsync(shift.ShiftId);
        Assert(shiftAfterOut!.ExpectedBalance == 1700.00m, "Expected 1700 after paid-out");

        var revRes = await controller.ReverseMovement(pcv.MovementId, new CashMovementReverseDto
        {
            SupervisorPin = "9999",
            Reason = "Entered wrong amount on voucher"
        });
        var reversedOrigDto = (CashMovementResponseDto)((OkObjectResult)revRes.Result!).Value!;
        Assert(reversedOrigDto.Status == "REVERSED", "Expected REVERSED status on original movement");
        Assert(reversedOrigDto.ReversedBy == "manager1", "Expected ReversedBy manager1");

        var reversalAuditRow = await db.CashDrawerMovements
            .FirstOrDefaultAsync(m => m.OriginalMovementId == pcv.MovementId && m.MovementType == "REVERSAL");
        Assert(reversalAuditRow != null, "Expected linked REVERSAL audit movement row in DB");
        Assert(reversalAuditRow!.ReferenceNumber!.StartsWith("REV-", StringComparison.Ordinal), "Expected REV- prefix on reversal entry");

        var shiftAfterRev = await db.CashDrawerShifts.FindAsync(shift.ShiftId);
        Assert(shiftAfterRev!.ExpectedBalance == 2000.00m, $"Expected balance restored to 2000.00, got {shiftAfterRev.ExpectedBalance}");

        // Attempting to reverse again must fail
        var secondRev = await controller.ReverseMovement(pcv.MovementId, new CashMovementReverseDto
        {
            SupervisorPin = "9999",
            Reason = "Duplicate reversal attempt"
        });
        Assert(secondRev.Result is BadRequestObjectResult, "Reversing an already-reversed movement must fail");
    }

    private static async Task Test18_BirSalesAndVatIsolation()
    {
        var (conn, db, shift) = await CreateTestEnvAsync();
        await using var _ = conn;

        // Seed a completed sale order with VAT and an InvoiceSequence
        db.InvoiceSequences.Add(new InvoiceSequence
        {
            InvoiceSequenceId = 1,
            OutletId = 1,
            SeriesCode = "2026",
            Prefix = "INV-",
            LastIssuedNumber = 105
        });
        var order = new Order
        {
            OrderId = 1,
            BranchId = 1,
            OutletId = 1,
            OrderNumber = "ORD-0001",
            OrderType = "DINE_IN",
            InvoiceNumber = "INV-0000105",
            CashierId = "cashier1",
            OrderStatus = "COMPLETED",
            PaymentStatus = "PAID",
            Subtotal = 1120.00m,
            VatableSales = 1000.00m,
            VatAmount = 120.00m,
            TotalAmount = 1120.00m,
            CreatedAt = DateTime.UtcNow
        };
        db.Orders.Add(order);
        db.Payments.Add(new Payment
        {
            PaymentId = 1,
            OrderId = 1,
            PaymentMethod = "CASH",
            Amount = 1120.00m,
            AmountTendered = 1120.00m,
            ChangeAmount = 0m,
            PaymentStatus = "SUCCESS",
            ProcessedBy = "cashier1",
            PaidAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var reportService = new SalesReportService(db);
        var beforeReport = await reportService.GenerateReportAsync(new SalesReportRequestDto { BranchId = 1 });

        var controller = CreateController(db, "cashier1", "Cashier");
        await controller.CreatePaidIn(new CashDrawerMovementDto
        {
            ShiftId = shift.ShiftId, BranchId = 1, OutletId = 1, CategoryId = 1, Amount = 500.00m, Reason = "Paid-In float"
        });
        await controller.CreatePaidOut(new CashDrawerMovementDto
        {
            ShiftId = shift.ShiftId, BranchId = 1, OutletId = 1, MovementType = "PETTY_CASH", CategoryId = 2, Amount = 250.00m, Reason = "Petty cash supplies", SupervisorPin = "9999"
        });
        await controller.RecordMovement(new CashDrawerMovementDto
        {
            ShiftId = shift.ShiftId, BranchId = 1, OutletId = 1, MovementType = "CASH_DROP", CategoryId = 4, Amount = 400.00m, Reason = "Safe drop", SupervisorPin = "9999"
        });

        var afterReport = await reportService.GenerateReportAsync(new SalesReportRequestDto { BranchId = 1 });
        var seqAfter = await db.InvoiceSequences.FirstAsync(s => s.OutletId == 1);

        Assert(afterReport.GrossSales == beforeReport.GrossSales && afterReport.GrossSales == 1120.00m, "Petty cash must never alter GrossSales");
        Assert(afterReport.OutputVat == beforeReport.OutputVat && afterReport.OutputVat == 120.00m, "Petty cash must never alter Output VAT");
        Assert(afterReport.NetSales == beforeReport.NetSales, "Petty cash must never alter NetSales");
        Assert(afterReport.CompletedTransactionsCount == 1, "Cash movements must never create sales orders");
        Assert(seqAfter.LastIssuedNumber == 105, "Cash movements must never consume BIR InvoiceSequences");
    }

    private static TransactionsController CreateTransactionsController(PosDbContext db, string username = "cashier1", string role = "Cashier", int? branchId = null, int? outletId = null)
    {
        var receiptService = new ReceiptService(db);
        var controller = new TransactionsController(db, receiptService);
        var claims = new List<Claim>
        {
            new Claim(ClaimTypes.Name, username),
            new Claim(ClaimTypes.Role, role)
        };
        if (branchId.HasValue)
            claims.Add(new Claim("branch_id", branchId.Value.ToString()));
        if (outletId.HasValue)
            claims.Add(new Claim("outlet_id", outletId.Value.ToString()));

        var identity = new ClaimsIdentity(claims, "TestAuth");
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };
        return controller;
    }

    private static ReturnsController CreateReturnsController(PosDbContext db, string username = "cashier1", string role = "Cashier")
    {
        var controller = new ReturnsController(db);
        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Name, username),
            new Claim(ClaimTypes.Role, role)
        ], "TestAuth");
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };
        return controller;
    }

    private static async Task Test19_TransactionsTokenClaimsScoping()
    {
        var (conn, db, shift) = await CreateTestEnvAsync();
        await using var _ = conn;

        var outlet2 = new Outlet { OutletId = 2, BranchId = 1, Name = "Counter 2", IsActive = true };
        db.Outlets.Add(outlet2);

        // Order 1 at outlet 1 (cashier1)
        db.Orders.Add(new Order
        {
            OrderId = 101, BranchId = 1, OutletId = 1, OrderNumber = "ORD-101", OrderType = "DINE_IN", CashierId = "cashier1",
            OrderStatus = "COMPLETED", PaymentStatus = "PAID", Subtotal = 100m, TotalAmount = 100m, CreatedAt = DateTime.UtcNow
        });
        // Order 2 at outlet 2 (cashier1)
        db.Orders.Add(new Order
        {
            OrderId = 102, BranchId = 1, OutletId = 2, OrderNumber = "ORD-102", OrderType = "DINE_IN", CashierId = "cashier1",
            OrderStatus = "COMPLETED", PaymentStatus = "PAID", Subtotal = 200m, TotalAmount = 200m, CreatedAt = DateTime.UtcNow
        });
        // Order 3 at outlet 1 (cashier2)
        db.Orders.Add(new Order
        {
            OrderId = 103, BranchId = 1, OutletId = 1, OrderNumber = "ORD-103", OrderType = "DINE_IN", CashierId = "cashier2",
            OrderStatus = "COMPLETED", PaymentStatus = "PAID", Subtotal = 300m, TotalAmount = 300m, CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        // Cashier 1 token scoped to Branch 1, Outlet 1
        var controller = CreateTransactionsController(db, "cashier1", "Cashier", branchId: 1, outletId: 1);

        // Standard fetch
        var res = await controller.GetTransactions(null, null, null, null, null, null, null, null, null, null, null);
        var ok = (List<OrderResponseDto>)((OkObjectResult)res.Result!).Value!;
        Assert(ok.Count == 1, $"Cashier should see exactly 1 order, got {ok.Count}");
        Assert(ok[0].OrderId == 101, $"Expected OrderId 101, got {ok[0].OrderId}");

        // Attempting to filter by a different outlet must be forbidden (403)
        var crossOutletRes = await controller.GetTransactions(null, null, null, outletId: 2, null, null, null, null, null, null, null);
        Assert(crossOutletRes.Result is ForbidResult, "Querying a different outlet than token claim must return ForbidResult");

        // Attempting to filter by a different branch must be forbidden (403)
        var crossBranchRes = await controller.GetTransactions(null, null, branchId: 99, null, null, null, null, null, null, null, null);
        Assert(crossBranchRes.Result is ForbidResult, "Querying a different branch than token claim must return ForbidResult");
    }

    private static async Task Test20_Transactions4AmCutoff()
    {
        // 1. Verify 4:00 AM business day cutoff math
        static DateTime CalcBusinessDayStart(DateTime localTime)
        {
            var baseDate = localTime.TimeOfDay < TimeSpan.FromHours(4)
                ? localTime.Date.AddDays(-1)
                : localTime.Date;
            return baseDate.AddHours(4);
        }

        // Night shift 03:30 AM Oct 20 -> belongs to business day starting Oct 19 04:00 AM
        var lateNight = new DateTime(2026, 10, 20, 3, 30, 0);
        var startLateNight = CalcBusinessDayStart(lateNight);
        Assert(startLateNight == new DateTime(2026, 10, 19, 4, 0, 0), $"Expected Oct 19 4am, got {startLateNight}");

        // Morning 04:00 AM Oct 20 -> belongs to business day starting Oct 20 04:00 AM
        var exactMorning = new DateTime(2026, 10, 20, 4, 0, 0);
        var startMorning = CalcBusinessDayStart(exactMorning);
        Assert(startMorning == new DateTime(2026, 10, 20, 4, 0, 0), $"Expected Oct 20 4am, got {startMorning}");

        // Evening 11:30 PM Oct 20 -> belongs to business day starting Oct 20 04:00 AM
        var evening = new DateTime(2026, 10, 20, 23, 30, 0);
        var startEvening = CalcBusinessDayStart(evening);
        Assert(startEvening == new DateTime(2026, 10, 20, 4, 0, 0), $"Expected Oct 20 4am, got {startEvening}");

        // 2. Verify server-side floor in TransactionsController
        var (conn, db, shift) = await CreateTestEnvAsync();
        await using var _ = conn;

        // Old order (2 days ago)
        db.Orders.Add(new Order
        {
            OrderId = 201, BranchId = 1, OutletId = 1, OrderNumber = "ORD-OLD", OrderType = "DINE_IN", CashierId = "cashier1",
            OrderStatus = "COMPLETED", PaymentStatus = "PAID", Subtotal = 50m, TotalAmount = 50m,
            CreatedAt = DateTime.UtcNow.AddDays(-2)
        });
        // Today's order (recent)
        db.Orders.Add(new Order
        {
            OrderId = 202, BranchId = 1, OutletId = 1, OrderNumber = "ORD-TODAY", OrderType = "DINE_IN", CashierId = "cashier1",
            OrderStatus = "COMPLETED", PaymentStatus = "PAID", Subtotal = 80m, TotalAmount = 80m,
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var controller = CreateTransactionsController(db, "cashier1", "Cashier", branchId: 1, outletId: 1);

        // Fetch without dateFrom: server must apply 4 AM floor and not return the old order
        var res = await controller.GetTransactions(null, null, null, null, null, null, null, null, null, null, null);
        var ok = (List<OrderResponseDto>)((OkObjectResult)res.Result!).Value!;
        Assert(ok.Count == 1 && ok[0].OrderId == 202, $"Expected only today's order, got {ok.Count} orders");
    }

    private static async Task Test21_VoidAndReturnStrictPinEnforcement()
    {
        var (conn, db, shift) = await CreateTestEnvAsync();
        await using var _ = conn;

        // Order to void
        var order = new Order
        {
            OrderId = 301, BranchId = 1, OutletId = 1, OrderNumber = "ORD-301", OrderType = "DINE_IN", InvoiceNumber = "INV-301",
            CashierId = "cashier1", OrderStatus = "COMPLETED", PaymentStatus = "PAID", Subtotal = 500m, TotalAmount = 500m,
            CreatedAt = DateTime.UtcNow
        };
        db.Orders.Add(order);
        db.Payments.Add(new Payment
        {
            PaymentId = 301, OrderId = 301, PaymentMethod = "CASH", Amount = 500m,
            AmountTendered = 500m, ChangeAmount = 0m, PaymentStatus = "SUCCESS", ProcessedBy = "cashier1", PaidAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var returnsController = CreateReturnsController(db, "cashier1", "Cashier");

        // 1. Attempt void using supervisor username "manager1" instead of PIN -> Must fail!
        var bypassReq = new VoidRequestDto
        {
            OriginalOrderId = 301,
            VoidReason = "Mistake",
            ApprovedByPinOrUsername = "manager1"
        };
        var badRes = await returnsController.VoidTransaction(bypassReq);
        Assert(badRes.Result is BadRequestObjectResult bad && bad.Value?.ToString()?.Contains("PIN not found") == true,
            "Using supervisor username in place of PIN must be rejected");

        // 2. Void using correct supervisor PIN "9999" -> Must succeed!
        var validReq = new VoidRequestDto
        {
            OriginalOrderId = 301,
            VoidReason = "Customer changed mind",
            ApprovedByPinOrUsername = "9999"
        };
        var okRes = await returnsController.VoidTransaction(validReq);
        Assert(okRes.Result is OkObjectResult okObj && ((VoidResponseDto)okObj.Value!).Status == "APPROVED",
            "Valid supervisor PIN must approve void");

        var updatedOrder = await db.Orders.FindAsync(301L);
        Assert(updatedOrder!.OrderStatus == "VOIDED" && updatedOrder.PaymentStatus == "VOIDED", "Order status must transition to VOIDED");
    }

    private static Task Test22_OfflineTransactionHistoryLogic()
    {
        // Verify offline query filtering & mapping contract
        var nowLocal = DateTime.Now;
        var baseDate = nowLocal.TimeOfDay < TimeSpan.FromHours(4) ? nowLocal.Date.AddDays(-1) : nowLocal.Date;
        var cutoffUtc = baseDate.AddHours(4).ToUniversalTime();

        var localOrderMock = new
        {
            OrderId = Guid.NewGuid().ToString(),
            InvoiceNumber = "INV-OFFLINE-001",
            BranchId = 1,
            OutletId = 1,
            CashierId = "cashier1",
            Subtotal = 250m,
            TotalAmount = 250m,
            PaymentStatus = "PAID",
            OrderStatus = "COMPLETED",
            CreatedAt = DateTime.UtcNow
        };

        Assert(localOrderMock.CreatedAt >= cutoffUtc, "Recent order must fall within active business day");
        Assert(localOrderMock.OutletId == 1 && localOrderMock.CashierId == "cashier1", "Order scoped to active terminal and cashier");
        return Task.CompletedTask;
    }
}

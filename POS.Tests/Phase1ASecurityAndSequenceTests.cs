using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using POS.Api.Controllers;
using POS.Api.Models;
using POS.Shared.Models;

namespace POS.Tests;

public static partial class Program
{
    private static async Task TestPhase1A_SessionTokenNeverMintsAdmin()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<PosDbContext>()
            .UseSqlite(connection)
            .Options;

        using var db = new TestPosDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var bt = new BusinessType { Id = 1, Type = "FNB", Description = "Food" };
        var branch = new Branch { BranchId = 1, Name = "Main Branch", Code = "BR01", BusinessTypeId = 1, TaxType = "VAT-Registered", IsActive = true };
        var outlet = new Outlet { OutletId = 1, BranchId = 1, Name = "Main Outlet", OutletCode = "OUT01", IsActive = true };
        var terminal = new Terminal
        {
            TerminalId = 1, OutletId = 1, BranchId = 1, Name = "POS 1", TerminalCode = "TERM-01",
            Uuid = "term-uuid-12345", DeviceIdentifier = "term-uuid-12345", IsActive = true
        };
        var adminUser = new User
        {
            UserId = 1, Username = "admin", Role = "Admin", PasswordHash = "hash", PinCode = "9999"
        };

        db.BusinessTypes.Add(bt);
        db.Branches.Add(branch);
        db.Outlets.Add(outlet);
        db.Terminals.Add(terminal);
        db.Users.Add(adminUser);
        await db.SaveChangesAsync();

        var inMemorySettings = new Dictionary<string, string?>
        {
            { "Jwt:Key", "SuperSecretKeyForPhase1ATestsMustBe32BytesLong!" },
            { "Jwt:Issuer", "POSTestIssuer" },
            { "Jwt:Audience", "POSTestAudience" }
        };
        IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(inMemorySettings).Build();

        var controller = new TerminalsController(db, config);

        // 1. Request session-token with CashierUsername = "admin"
        var result = await controller.IssueSessionToken(new TerminalSessionTokenRequestDto
        {
            TerminalId = 1,
            DeviceUuid = "term-uuid-12345",
            CashierUsername = "admin"
        });

        Assert(result.Result is OkObjectResult, "IssueSessionToken must return 200 OK for valid terminal.");
        var okVal = (TerminalSessionTokenResponseDto)((OkObjectResult)result.Result!).Value!;
        Assert(okVal.Success && !string.IsNullOrWhiteSpace(okVal.Token), "Session token must be issued.");

        // Read claims from token
        var handler = new JwtSecurityTokenHandler();
        var jwt = handler.ReadJwtToken(okVal.Token);
        var roleClaim = jwt.Claims.FirstOrDefault(c => c.Type == ClaimTypes.Role || c.Type == "role")?.Value;
        Assert(roleClaim == "Terminal", $"Issued token role MUST be 'Terminal', but was '{roleClaim}'. Admin JWT minting without credentials blocked!");

        // 2. IP matching removed: Request with unauthenticated device identifier that does not match Uuid must fail
        var mismatchResult = await controller.IssueSessionToken(new TerminalSessionTokenRequestDto
        {
            TerminalId = 1,
            DeviceUuid = "unknown-uuid-99999"
        });
        Assert(mismatchResult.Result is UnauthorizedObjectResult, "IssueSessionToken must reject non-matching UUID.");

        // 3. Locked terminal enforcement: lock terminal and verify cashier cannot obtain session token
        terminal.ClosedBusinessDate = POS.Shared.Services.BusinessDayClock.GetBusinessDateString(DateTime.UtcNow);
        terminal.ClosingLockedByCashier = "admin";
        terminal.ClosingLockedAtUtc = DateTime.UtcNow;
        terminal.ClosingUnlockAtUtc = DateTime.UtcNow.AddHours(5);
        await db.SaveChangesAsync();

        var lockedResult = await controller.IssueSessionToken(new TerminalSessionTokenRequestDto
        {
            TerminalId = 1,
            DeviceUuid = "term-uuid-12345",
            CashierUsername = "admin"
        });
        Assert(lockedResult.Result is ObjectResult objRes && (objRes.StatusCode == 403 || objRes.StatusCode == 423),
            "IssueSessionToken must enforce terminal lock when cashier username is supplied.");
    }

    private static async Task TestPhase1A_OfflineReferenceDataPreservesInvoiceSequenceBlock()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<PosDbContext>()
            .UseSqlite(connection)
            .Options;

        using var db = new TestPosDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var bt = new BusinessType { Id = 1, Type = "FNB", Description = "Food" };
        var branch = new Branch { BranchId = 1, Name = "Main Branch", Code = "BR01", BusinessTypeId = 1, TaxType = "VAT-Registered", IsActive = true };
        var outlet = new Outlet { OutletId = 1, BranchId = 1, Name = "Main Outlet", OutletCode = "OUT01", IsActive = true };
        var terminal = new Terminal
        {
            TerminalId = 1, OutletId = 1, BranchId = 1, Name = "POS 1", TerminalCode = "TERM-01",
            Uuid = "term-uuid-1", IsActive = true
        };
        var activeSeq = new InvoiceSequence
        {
            InvoiceSequenceId = 10, TerminalId = 1, SeriesCode = "INV2026", Prefix = "SI-",
            StartingNumber = 1000, EndingNumber = 100000, LastIssuedNumber = 2000,
            PaddingLength = 7, IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };

        db.BusinessTypes.Add(bt);
        db.Branches.Add(branch);
        db.Outlets.Add(outlet);
        db.Terminals.Add(terminal);
        db.InvoiceSequences.Add(activeSeq);
        await db.SaveChangesAsync();

        var controller = new OfflineReferenceDataController(db);
        SetUserContext(controller, "TERM-01", "Terminal", branchId: 1, terminalId: 1);

        // 1. Client has active block (Starting: 1001, Ending: 2000, LastIssued: 1005). Remaining capacity = 995 (>= 100)
        var req = new OfflineReferenceDataRequestDto
        {
            TerminalId = 1,
            CurrentSequenceId = 10,
            SeriesCode = "INV2026",
            CurrentStartingNumber = 1001,
            CurrentEndingNumber = 2000,
            CurrentLastIssuedNumber = 1005
        };

        var res = await controller.GetReferenceData(1, req);
        Assert(res.Result is OkObjectResult, "GetReferenceData must succeed.");
        var responseDto = (OfflineReferenceDataResponseDto)((OkObjectResult)res.Result!).Value!;
        Assert(responseDto.InvoiceSequence != null, "InvoiceSequence must be returned.");
        Assert(responseDto.InvoiceSequence!.StartingNumber == 1001, "Existing block StartingNumber must be preserved.");
        Assert(responseDto.InvoiceSequence.EndingNumber == 2000, "Existing block EndingNumber must be preserved.");
        Assert(responseDto.InvoiceSequence.LastIssuedNumber == 1005, "Existing block LastIssuedNumber must be preserved.");

        // Check server DB sequence: LastIssuedNumber must NOT have jumped by 1,000!
        var seqInDb = await db.InvoiceSequences.FindAsync(10);
        Assert(seqInDb!.LastIssuedNumber == 2000,
            $"Server LastIssuedNumber must remain 2000 without burning numbers, but was {seqInDb.LastIssuedNumber}.");

        // 2. Client is low on capacity: remaining < 100 (LastIssued: 1950, Ending: 2000 -> remaining 50)
        var lowCapReq = new OfflineReferenceDataRequestDto
        {
            TerminalId = 1,
            CurrentSequenceId = 10,
            SeriesCode = "INV2026",
            CurrentStartingNumber = 1001,
            CurrentEndingNumber = 2000,
            CurrentLastIssuedNumber = 1950
        };

        var lowCapRes = await controller.GetReferenceData(1, lowCapReq);
        var lowCapDto = (OfflineReferenceDataResponseDto)((OkObjectResult)lowCapRes.Result!).Value!;
        Assert(lowCapDto.InvoiceSequence != null, "InvoiceSequence must be returned.");
        Assert(lowCapDto.InvoiceSequence!.StartingNumber == 2001, $"New block should start at 2001, but was {lowCapDto.InvoiceSequence.StartingNumber}.");
        Assert(lowCapDto.InvoiceSequence.EndingNumber == 3000, $"New block should end at 3000, but was {lowCapDto.InvoiceSequence.EndingNumber}.");

        var updatedSeqInDb = await db.InvoiceSequences.FindAsync(10);
        Assert(updatedSeqInDb!.LastIssuedNumber == 3000, "Server LastIssuedNumber must advance to 3000 for newly allocated block.");
    }
}

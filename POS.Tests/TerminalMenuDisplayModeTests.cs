using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using POS.Api.Controllers;
using POS.Api.Models;
using POS.Shared.Models;

namespace POS.Tests;

public static partial class Program
{
    private static async Task TestMenuDisplayModeRoundTrip()
    {
        var (conn, db, _) = await CreateTestEnvAsync(); await using var cleanup = conn;
        var api = new TerminalsController(db, new ConfigurationBuilder().Build()); Authenticate(api);
        (await db.Branches.SingleAsync()).Code = "BR1";
        await db.SaveChangesAsync();
        var created = await api.CreateTerminal(new TerminalDto { BranchId = 1, OutletId = 1, TerminalCode = "IMAGE", Name = "Image terminal", IpAddress = "10.20.30.40", MenuDisplayMode = MenuDisplayMode.ImageAndText });
        Assert(created.Result is CreatedAtActionResult, "Create must succeed.");
        var dto = (TerminalDto)((CreatedAtActionResult)created.Result!).Value!;
        Assert(dto.MenuDisplayMode == MenuDisplayMode.ImageAndText, "Create response must preserve image mode.");
        db.ChangeTracker.Clear();
        Assert((await db.Terminals.FindAsync(dto.TerminalId))!.MenuDisplayMode == MenuDisplayMode.ImageAndText, "Mode must persist.");
        var read = (TerminalDto)((OkObjectResult)(await api.GetTerminal(dto.TerminalId)).Result!).Value!;
        Assert(read.MenuDisplayMode == MenuDisplayMode.ImageAndText, "Get mapping must preserve image mode.");
        var list = (List<TerminalDto>)((OkObjectResult)(await api.GetTerminals(null, 1)).Result!).Value!;
        Assert(list.Single(t => t.TerminalId == dto.TerminalId).MenuDisplayMode == MenuDisplayMode.ImageAndText, "List mapping must preserve image mode.");
        var resolved = (TerminalConfigDto)((OkObjectResult)await api.ResolveTerminal("10.20.30.40")).Value!;
        Assert(resolved.MenuDisplayMode == MenuDisplayMode.ImageAndText, "Resolve must project image mode.");
        var offline = new OfflineReferenceDataController(db); Authenticate(offline);
        var reference = (OfflineReferenceDataResponseDto)((OkObjectResult)(await offline.GetReferenceData(dto.TerminalId)).Result!).Value!;
        Assert(reference.Terminal.MenuDisplayMode == MenuDisplayMode.ImageAndText, "Offline reference must preserve image mode.");
        dto.MenuDisplayMode = MenuDisplayMode.TextOnly;
        Assert(await api.UpdateTerminal(dto.TerminalId, dto) is NoContentResult, "Update must succeed.");
        db.ChangeTracker.Clear();
        Assert((await db.Terminals.FindAsync(dto.TerminalId))!.MenuDisplayMode == MenuDisplayMode.TextOnly, "Update must persist text mode.");
        Assert((await db.Terminals.FindAsync(1))!.MenuDisplayMode == MenuDisplayMode.TextOnly, "Sibling terminal must remain unchanged.");
    }

    private static async Task TestMenuDisplayModeInvalidServerValues()
    {
        var (conn, db, _) = await CreateTestEnvAsync(); await using var cleanup = conn;
        var api = new TerminalsController(db, new ConfigurationBuilder().Build()); Authenticate(api);
        foreach (var invalid in new[] { -1, 2, 99 })
        {
            var dto = new TerminalDto { BranchId = 1, OutletId = 1, TerminalCode = "INVALID", Name = "Invalid", MenuDisplayMode = (MenuDisplayMode)invalid };
            var result = await api.CreateTerminal(dto);
            Assert(result.Result is BadRequestObjectResult, $"Create must explicitly reject mode {invalid}, even without MVC validation.");
            dto.TerminalCode = (await db.Terminals.FindAsync(1))!.TerminalCode;
            Assert(await api.UpdateTerminal(1, dto) is BadRequestObjectResult, $"Update must explicitly reject mode {invalid}.");
            db.ChangeTracker.Clear();
            Assert((await db.Terminals.FindAsync(1))!.MenuDisplayMode == MenuDisplayMode.TextOnly, "Invalid update must not alter persisted mode.");
            Assert(await db.Terminals.CountAsync() == 1, "Invalid create must not insert a terminal.");
        }
    }

    private static async Task TestMenuDisplayModeAuthorization()
    {
        foreach (var methodName in new[] { "CreateTerminal", "UpdateTerminal", "GetTerminal", "GetTerminals", "DeleteTerminal" })
        {
            var attributes = typeof(TerminalsController).GetMethod(methodName)!.GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), true)
                .Cast<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>();
            Assert(attributes.Any(a => a.Roles == "Admin,Manager"), $"{methodName} must remain supervisor-only in MVC.");
        }
        Assert(typeof(TerminalsController).GetMethod("ResolveTerminal")!.IsDefined(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), true), "Resolution must remain authenticated.");
        Assert(typeof(OfflineReferenceDataController).IsDefined(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), true), "Offline reference must remain authenticated.");
        var (conn, db, _) = await CreateTestEnvAsync(); await using var cleanup = conn;
        db.Branches.Add(new Branch { BranchId = 2, Name = "Other", Code = "BR2", IsActive = true, TaxType = "VAT-Registered" });
        db.Outlets.Add(new Outlet { OutletId = 2, BranchId = 2, OutletCode = "OTHER", Name = "Other", IsActive = true });
        db.Terminals.Add(new Terminal { TerminalId = 2, BranchId = 2, OutletId = 2, TerminalCode = "OTHER", Name = "Other", IsActive = true });
        await db.SaveChangesAsync();
        var api = new TerminalsController(db, new ConfigurationBuilder().Build()); Authenticate(api, "Manager");
        var dto = new TerminalDto { TerminalId = 2, BranchId = 2, OutletId = 2, TerminalCode = "OTHER", Name = "Other", MenuDisplayMode = MenuDisplayMode.ImageAndText };
        Assert(await api.UpdateTerminal(2, dto) is ForbidResult, "Manager must not update another branch's mode.");
        Assert((await api.CreateTerminal(dto)).Result is ForbidResult, "Manager must not create in another branch.");
        Assert((await api.GetTerminal(2)).Result is ForbidResult, "Manager must not read another branch.");
        var own = (List<TerminalDto>)((OkObjectResult)(await api.GetTerminals(null, null)).Result!).Value!;
        Assert(own.All(t => t.BranchId == 1), "Manager list must remain branch-scoped.");
        var offline = new OfflineReferenceDataController(db); Authenticate(offline, "Cashier");
        Assert((await offline.GetReferenceData(2)).Result is ForbidResult, "Cashier must not read another terminal configuration.");
        Authenticate(offline, "Manager");
        Assert((await offline.GetReferenceData(2)).Result is ForbidResult, "Manager must not read another branch's offline configuration.");
        dto.TerminalId = 1; dto.BranchId = 1; dto.OutletId = 1; dto.TerminalCode = (await db.Terminals.FindAsync(1))!.TerminalCode;
        Assert(await api.UpdateTerminal(1, dto) is NoContentResult, "Manager may change own branch terminal mode.");
        Authenticate(api, "Admin"); dto.TerminalId = 2; dto.BranchId = 2; dto.OutletId = 2; dto.TerminalCode = "OTHER";
        Assert(await api.UpdateTerminal(2, dto) is NoContentResult, "Admin may change another branch terminal mode.");
    }

    private static Task TestMenuDisplayModeDatabaseModel()
    {
        using var db = new PosDbContext(new DbContextOptionsBuilder<PosDbContext>().UseNpgsql("Host=127.0.0.1;Database=never_connected;Username=unused").Options);
        var model = Microsoft.EntityFrameworkCore.Infrastructure.AccessorExtensions.GetService<Microsoft.EntityFrameworkCore.Metadata.IDesignTimeModel>(db).Model;
        var terminal = model.FindEntityType(typeof(Terminal))!;
        var property = terminal.FindProperty("MenuDisplayMode")!;
        Assert(property.GetColumnType() == "integer" && !property.IsNullable, "PostgreSQL mode must be a required integer.");
        Assert(Convert.ToInt32(property.GetDefaultValue()) == 0 && property.GetDefaultValue() != null, "Database default must be explicitly TextOnly (0).");
        Assert(terminal.GetCheckConstraints().Any(c => c.Name == "CK_Terminals_MenuDisplayMode" && c.Sql == "\"MenuDisplayMode\" IN (0, 1)"), "Database must constrain modes to 0/1.");
        Assert(db.Database.GetMigrations().Any(m => m.EndsWith("_TerminalMenuDisplayMode")), "A deployable EF migration must exist.");
        var migration = new POS.Api.Migrations.TerminalMenuDisplayMode();
        var sql = migration.UpOperations.OfType<Microsoft.EntityFrameworkCore.Migrations.Operations.SqlOperation>().SingleOrDefault()?.Sql;
        Assert(sql?.Contains("IF NOT EXISTS", StringComparison.Ordinal) == true && sql.Contains("CK_Terminals_MenuDisplayMode", StringComparison.Ordinal), "Migration must guard additive DDL for manual-first deployment.");
        try
        {
            _ = migration.DownOperations;
            Assert(false, "Downgrade must refuse destructive mode loss.");
        }
        catch (NotSupportedException) { }
        return Task.CompletedTask;
    }

    private static Task TestMenuDisplayModeContracts()
    {
        var enumType = typeof(TerminalDto).Assembly.GetType("POS.Shared.Models.MenuDisplayMode");
        Assert(enumType?.IsEnum == true, "Shared MenuDisplayMode enum must exist.");
        Assert(Enum.GetName(enumType!, 0) == "TextOnly" && Enum.GetName(enumType!, 1) == "ImageAndText", "Stable numeric enum contract must be 0/1.");
        foreach (var type in new[] { typeof(TerminalDto), typeof(TerminalConfigDto), typeof(OfflineTerminalConfigDto), typeof(Terminal) })
        {
            var property = type.GetProperty("MenuDisplayMode");
            Assert(property?.PropertyType == enumType, $"{type.Name} must expose the shared enum.");
            var legacy = JsonSerializer.Deserialize("{}", type)!;
            Assert(Convert.ToInt32(property!.GetValue(legacy)) == 0, $"{type.Name} missing JSON field must default to TextOnly.");
            var modern = JsonSerializer.Deserialize("{\"MenuDisplayMode\":1}", type)!;
            Assert(Convert.ToInt32(property.GetValue(modern)) == 1, "Numeric JSON mode must survive deserialization.");
        }
        var dto = new TerminalDto { OutletId = 1, TerminalCode = "T", Name = "Terminal" };
        typeof(TerminalDto).GetProperty("MenuDisplayMode")!.SetValue(dto, Enum.ToObject(enumType!, 99));
        var results = new List<ValidationResult>();
        Assert(!Validator.TryValidateObject(dto, new ValidationContext(dto), results, true)
            && results.Any(r => r.MemberNames.Contains("MenuDisplayMode")), "DTO validation must reject undefined enum values.");
        return Task.CompletedTask;
    }
}

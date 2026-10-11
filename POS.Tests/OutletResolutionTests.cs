using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using POS.Api.Controllers;
using POS.Api.Models;
using Xunit;

namespace POS.Tests;

public class OutletResolutionTests
{
    private sealed class TestDb(DbContextOptions<PosDbContext> options) : PosDbContext(options)
    {
        protected override void OnConfiguring(DbContextOptionsBuilder builder) { }
    }
    [Theory]
    [InlineData("not-an-ip")]
    [InlineData("127.0.0.1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("::ffff:0.0.0.0")]
    [InlineData("224.0.0.1")]
    [InlineData("255.255.255.255")]
    public async Task Invalid_device_addresses_are_rejected(string address)
    {
        await using var db = new TestDb(new DbContextOptionsBuilder<PosDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        Assert.IsType<BadRequestObjectResult>(await new OutletsController(db).ResolveTerminal(address));
    }

    [Theory]
    [InlineData("missing", 404)]
    [InlineData("duplicate", 409)]
    [InlineData("inactive", 409)]
    [InlineData("branch-inactive", 409)]
    public async Task Missing_ambiguous_or_inactive_terminal_is_blocked(string mode, int status)
    {
        await using var db = new TestDb(new DbContextOptionsBuilder<PosDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        db.Branches.Add(new Branch { BranchId = 1, Name = "Test", Code = "BR01", IsActive = mode != "branch-inactive", TaxType = "VAT" });
        if (mode != "missing") db.Outlets.Add(new Outlet { OutletId = 1, BranchId = 1, Name = "A", DeviceIdentifier = "192.168.1.25", IsActive = mode != "inactive" });
        if (mode == "duplicate") db.Outlets.Add(new Outlet { OutletId = 2, BranchId = 1, Name = "B", DeviceIdentifier = "::ffff:192.168.1.25", IsActive = true });
        await db.SaveChangesAsync();
        Assert.Equal(status, ((ObjectResult)await new OutletsController(db).ResolveTerminal("192.168.1.25")).StatusCode);
    }

    [Fact]
    public async Task Authenticated_lookup_returns_branch_code_and_outlet_name()
    {
        var method = typeof(OutletsController).GetMethod("ResolveTerminal");
        Assert.NotNull(method);
        Assert.NotNull(Attribute.GetCustomAttribute(method, typeof(AuthorizeAttribute)));
        await using var db = new TestDb(new DbContextOptionsBuilder<PosDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        db.Branches.Add(new Branch { BranchId = 1, Name = "Test", Code = "BR01", IsActive = true, TaxType = "VAT" });
        db.Outlets.Add(new Outlet { OutletId = 1, BranchId = 1, Name = "Counter A", DeviceIdentifier = "192.168.1.25", IsActive = true });
        await db.SaveChangesAsync();
        var task = (Task<IActionResult>)method.Invoke(new OutletsController(db), new object[] { "192.168.1.25" })!;
        var result = Assert.IsType<OkObjectResult>(await task);
        var json = System.Text.Json.JsonSerializer.Serialize(result.Value);
        Assert.Contains("BR01", json);
        Assert.Contains("Counter A", json);
    }
}

using Microsoft.EntityFrameworkCore;
using Npgsql;
using POS.Api.Models;
using Xunit;

namespace POS.Tests;

public sealed class LocalPostgresFactAttribute : FactAttribute
{
    public LocalPostgresFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("POS_TEST_POSTGRES") != "1")
            Skip = "Opt in with POS_TEST_POSTGRES=1; uses a temporary schema in loopback pos_db only.";
    }
}

public class BusinessTypePostgresTests
{
    [LocalPostgresFact]
    public async Task Script_is_idempotent_and_enforces_relational_constraints()
    {
        using var configured = new PosDbContext();
        var cs = new NpgsqlConnectionStringBuilder(configured.Database.GetConnectionString());
        Assert.Contains(cs.Host, new[] { "localhost", "127.0.0.1", "::1" });
        Assert.Equal("pos_db", cs.Database);
        cs.IncludeErrorDetail = false;
        cs.Pooling = false;
        await using var db = new NpgsqlConnection(cs.ConnectionString);
        await db.OpenAsync();
        var schema = "business_type_test_" + Guid.NewGuid().ToString("N");
        async Task Execute(string sql) { await using var cmd = new NpgsqlCommand(sql, db); await cmd.ExecuteNonQueryAsync(); }
        async Task<object?> Scalar(string sql) { await using var cmd = new NpgsqlCommand(sql, db); return await cmd.ExecuteScalarAsync(); }
        await Execute($"CREATE SCHEMA {schema}; SET search_path TO {schema};");
        try
        {
            await Execute("CREATE TABLE \"Branches\" (\"BranchId\" integer PRIMARY KEY); INSERT INTO \"Branches\" VALUES (1);");
            var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../.."));
            while (!File.Exists(Path.Combine(root, "PROJECT_CONTEXT.md"))) root = Directory.GetParent(root)!.FullName;
            var sql = await File.ReadAllTextAsync(Path.Combine(root, "POS.Api/Database/BusinessTypes.sql"));
            await Execute(sql);
            await Execute(sql);
            Assert.Equal(4L, await Scalar("SELECT count(*) FROM \"BusinessType\""));
            Assert.Equal(1L, await Scalar("SELECT count(*) FROM \"Branches\" WHERE \"BusinessTypeId\" IS NULL"));
            Assert.Equal("Combination of retail and F&B or services", await Scalar("SELECT description FROM \"BusinessType\" WHERE type='HYBRID'"));
            var unique = await Assert.ThrowsAsync<PostgresException>(() => Execute("INSERT INTO \"BusinessType\" (type,description) VALUES ('RETAIL','Duplicate')"));
            Assert.Equal("23505", unique.SqlState);
            var invalid = await Assert.ThrowsAsync<PostgresException>(() => Execute("UPDATE \"Branches\" SET \"BusinessTypeId\"=999999"));
            Assert.Equal("23503", invalid.SqlState);
            await Execute("UPDATE \"Branches\" SET \"BusinessTypeId\"=(SELECT id FROM \"BusinessType\" WHERE type='RETAIL')");
            var referenced = await Assert.ThrowsAsync<PostgresException>(() => Execute("DELETE FROM \"BusinessType\" WHERE type='RETAIL'"));
            Assert.Equal("23001", referenced.SqlState);
        }
        finally { await Execute($"ROLLBACK; SET search_path TO public; DROP SCHEMA {schema} CASCADE;"); }
    }
}

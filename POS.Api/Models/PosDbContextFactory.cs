using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace POS.Api.Models;

// Design-time schema generation never starts the API or its legacy migration runner.
public sealed class PosDbContextFactory : IDesignTimeDbContextFactory<PosDbContext>
{
    public PosDbContext CreateDbContext(string[] args)
    {
        return new PosDbContext(new DbContextOptionsBuilder<PosDbContext>()
            .UseNpgsql(Environment.GetEnvironmentVariable("POS_MIGRATION_CONNECTION")
                ?? "Host=127.0.0.1;Database=pos_schema_design_only;Username=not_configured")
            .Options);
    }
}

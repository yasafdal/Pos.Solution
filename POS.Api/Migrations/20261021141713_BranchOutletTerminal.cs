using System.Reflection;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace POS.Api.Migrations;

/// <summary>In-place upgrade of the existing scaffolded production schema; not a fresh database initializer.</summary>
public partial class BranchOutletTerminal : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("POS.Api.Migrations.BranchOutletTerminal.sql")
            ?? throw new InvalidOperationException("The hierarchy migration SQL resource is missing.");
        using var reader = new StreamReader(stream);
        migrationBuilder.Sql(reader.ReadToEnd());
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Automatic downgrade could discard new physical outlets. Stop writes and restore the validated pre-migration backup, or perform an audited forward repair.");
}


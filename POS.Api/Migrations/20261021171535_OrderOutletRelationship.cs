using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace POS.Api.Migrations
{
    /// <inheritdoc />
    public partial class OrderOutletRelationship : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = current_schema() AND table_name = 'Orders' AND column_name = 'OutletId') THEN
        ALTER TABLE ""Orders"" ADD COLUMN ""OutletId"" integer NULL;
        UPDATE ""Orders"" o SET ""OutletId"" = t.""OutletId"" FROM ""Terminals"" t WHERE t.""TerminalId"" = o.""TerminalId"";
        ALTER TABLE ""Orders"" ALTER COLUMN ""OutletId"" SET NOT NULL;
        ALTER TABLE ""Orders"" ADD CONSTRAINT ""FK_Orders_Outlets_OutletId"" FOREIGN KEY (""OutletId"") REFERENCES ""Outlets""(""OutletId"") ON DELETE RESTRICT;
        CREATE INDEX ""IX_Orders_OutletId"" ON ""Orders"" (""OutletId"");
    END IF;
END $$;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Downgrade must not discard order outlet history. Restore backup while writes are stopped.");
        }
    }
}

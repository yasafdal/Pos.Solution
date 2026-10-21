using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace POS.Api.Migrations
{
    /// <inheritdoc />
    public partial class OrderServiceModeAndTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = current_schema() AND table_name = 'Orders' AND column_name = 'HeadCount') THEN
        ALTER TABLE ""Orders"" ADD COLUMN ""HeadCount"" integer NULL;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = current_schema() AND table_name = 'Orders' AND column_name = 'TableNumber') THEN
        ALTER TABLE ""Orders"" ADD COLUMN ""TableNumber"" character varying(20) NULL;
    END IF;
END $$;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Downgrade must not discard order table or head count history.");
        }
    }
}

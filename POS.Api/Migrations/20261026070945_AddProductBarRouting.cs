using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace POS.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddProductBarRouting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = current_schema() AND table_name = 'Products' AND column_name = 'IsBarItem') THEN
                        ALTER TABLE "Products" ADD COLUMN "IsBarItem" boolean NOT NULL DEFAULT false;
                    END IF;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Downgrade must not discard product preparation routing configuration. Restore backup while writes are stopped.");
        }
    }
}

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace POS.Api.Migrations
{
    public partial class TerminalMenuDisplayMode : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE "Terminals" ADD COLUMN IF NOT EXISTS "MenuDisplayMode" integer NOT NULL DEFAULT 0;
                DO $menu_mode$
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = current_schema() AND table_name = 'Terminals' AND column_name = 'MenuDisplayMode' AND data_type = 'integer') THEN
                        RAISE EXCEPTION 'Terminals.MenuDisplayMode must be integer; reconcile schema before deployment';
                    END IF;
                    IF EXISTS (SELECT 1 FROM "Terminals" WHERE "MenuDisplayMode" IS NULL OR "MenuDisplayMode" NOT IN (0, 1)) THEN
                        RAISE EXCEPTION 'Invalid terminal menu display modes; reconcile data before deployment';
                    END IF;
                    ALTER TABLE "Terminals" ALTER COLUMN "MenuDisplayMode" SET DEFAULT 0;
                    ALTER TABLE "Terminals" ALTER COLUMN "MenuDisplayMode" SET NOT NULL;
                    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = '"Terminals"'::regclass AND conname = 'CK_Terminals_MenuDisplayMode') THEN
                        ALTER TABLE "Terminals" ADD CONSTRAINT "CK_Terminals_MenuDisplayMode" CHECK ("MenuDisplayMode" IN (0, 1));
                    END IF;
                    ALTER TABLE "Terminals" VALIDATE CONSTRAINT "CK_Terminals_MenuDisplayMode";
                END $menu_mode$;
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Retain the additive terminal menu display schema when rolling back application binaries. Restore a verified backup under DBA control if schema removal is required.");
        }
    }
}

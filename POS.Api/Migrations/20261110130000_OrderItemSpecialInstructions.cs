using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace POS.Api.Migrations
{
    public partial class OrderItemSpecialInstructions : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE "OrderItems" ADD COLUMN IF NOT EXISTS "SpecialInstructions" character varying(250) NULL;
                ALTER TABLE "HeldSaleItems" ADD COLUMN IF NOT EXISTS "SpecialInstructions" character varying(250) NULL;
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Additive special instructions columns must be retained when rolling back application binaries.");
        }
    }
}

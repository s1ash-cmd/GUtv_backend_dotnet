using GUtv_backend_dotnet.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace GUtv_backend_dotnet.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20261108000000_ClearDashAdminComments")]
public class ClearDashAdminComments : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // A Telegram "skip" reply that was not exactly "-" (spaces, en/em dash)
        // was stored as "<admin name>: -". Such comments carry no text.
        migrationBuilder.Sql(
            """
            UPDATE "Bookings"
            SET "AdminComment" = NULL
            WHERE "AdminComment" IS NOT NULL
              AND EXISTS (
                  SELECT 1 FROM "Users"
                  WHERE "Bookings"."AdminComment" IN (
                      "Users"."Name" || ': -',
                      "Users"."Name" || ': –',
                      "Users"."Name" || ': —'
                  )
              );
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // The removed comments contained no text; there is nothing to restore.
    }
}

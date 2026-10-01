using GUtv_backend_dotnet.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace GUtv_backend_dotnet.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20261001000000_ClearSyntheticAdminComments")]
public class ClearSyntheticAdminComments : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Empty decisions previously stored only the administrator's display name.
        // Match current names exactly, including users whose administrator role was revoked.
        // A real formatted comment contains a colon; retain it even if it matches another name.
        migrationBuilder.Sql(
            """
            UPDATE "Bookings"
            SET "AdminComment" = NULL
            WHERE "AdminComment" IS NOT NULL
              AND "AdminComment" NOT LIKE '%:%'
              AND EXISTS (
                  SELECT 1 FROM "Users"
                  WHERE "Users"."Name" = "Bookings"."AdminComment"
              );
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // The historical author was not recorded; reconstructing a comment would invent data.
    }
}

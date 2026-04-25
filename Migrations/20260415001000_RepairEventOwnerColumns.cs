using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GUtv_backend_dotnet.Migrations
{
    /// <inheritdoc />
    public partial class RepairEventOwnerColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                ALTER TABLE "Events"
                ADD COLUMN IF NOT EXISTS "UserId" integer NULL;
                """);

            migrationBuilder.Sql(
                """
                CREATE INDEX IF NOT EXISTS "IX_Events_UserId" ON "Events" ("UserId");
                """);

            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF NOT EXISTS (
                        SELECT 1 FROM pg_constraint WHERE conname = 'FK_Events_Users_UserId'
                    ) THEN
                        ALTER TABLE "Events"
                        ADD CONSTRAINT "FK_Events_Users_UserId"
                        FOREIGN KEY ("UserId") REFERENCES "Users" ("Id") ON DELETE SET NULL;
                    END IF;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF EXISTS (
                        SELECT 1 FROM pg_constraint WHERE conname = 'FK_Events_Users_UserId'
                    ) THEN
                        ALTER TABLE "Events" DROP CONSTRAINT "FK_Events_Users_UserId";
                    END IF;
                END $$;
                """);

            migrationBuilder.Sql(
                """
                DROP INDEX IF EXISTS "IX_Events_UserId";
                """);

            migrationBuilder.Sql(
                """
                ALTER TABLE "Events" DROP COLUMN IF EXISTS "UserId";
                """);
        }
    }
}

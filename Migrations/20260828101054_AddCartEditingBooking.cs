using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GUtv_backend_dotnet.Migrations
{
    /// <inheritdoc />
    public partial class AddCartEditingBooking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Cart tables may predate their migration history after a manual setup.
            migrationBuilder.Sql(
                """
                ALTER TABLE "Carts" ADD COLUMN IF NOT EXISTS "EditingBookingId" integer;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EditingBookingId",
                table: "Carts");
        }
    }
}

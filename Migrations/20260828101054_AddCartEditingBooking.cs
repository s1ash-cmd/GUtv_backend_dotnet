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
            migrationBuilder.AddColumn<int>(
                name: "EditingBookingId",
                table: "Carts",
                type: "integer",
                nullable: true);
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

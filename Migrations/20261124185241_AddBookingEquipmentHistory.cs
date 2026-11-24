using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GUtv_backend_dotnet.Migrations
{
    /// <inheritdoc />
    public partial class AddBookingEquipmentHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BookingEquipmentChanges",
                columns: table => new
                {
                    BookingId = table.Column<int>(type: "integer", nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    ActorUserId = table.Column<int>(type: "integer", nullable: false),
                    ActorName = table.Column<string>(type: "text", nullable: false),
                    ChangedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EquipmentJson = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BookingEquipmentChanges", x => new { x.BookingId, x.Revision });
                    table.ForeignKey(
                        name: "FK_BookingEquipmentChanges_Bookings_BookingId",
                        column: x => x.BookingId,
                        principalTable: "Bookings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BookingEquipmentChanges");
        }
    }
}

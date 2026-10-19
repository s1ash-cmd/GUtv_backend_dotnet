using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GUtv_backend_dotnet.Migrations
{
    /// <inheritdoc />
    public partial class AddInventorySelections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int[]>(
                name: "SelectedItemIds",
                table: "CartItems",
                type: "integer[]",
                nullable: false,
                defaultValue: new int[0]);

            migrationBuilder.AddColumn<bool>(
                name: "IsExplicitlySelected",
                table: "BookingItems",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SelectedItemIds",
                table: "CartItems");

            migrationBuilder.DropColumn(
                name: "IsExplicitlySelected",
                table: "BookingItems");
        }
    }
}

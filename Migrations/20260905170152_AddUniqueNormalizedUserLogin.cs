using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GUtv_backend_dotnet.Migrations
{
    /// <inheritdoc />
    public partial class AddUniqueNormalizedUserLogin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "NormalizedLogin",
                table: "Users",
                type: "text",
                nullable: false,
                computedColumnSql: "lower(btrim(\"Login\"))",
                stored: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_NormalizedLogin",
                table: "Users",
                column: "NormalizedLogin",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Users_NormalizedLogin",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "NormalizedLogin",
                table: "Users");
        }
    }
}

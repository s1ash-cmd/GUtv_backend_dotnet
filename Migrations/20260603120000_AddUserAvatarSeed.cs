using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GUtv_backend_dotnet.Migrations
{
    /// <inheritdoc />
    public partial class AddUserAvatarSeed : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AvatarSeed",
                table: "Users",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AvatarSeed",
                table: "Users");
        }
    }
}

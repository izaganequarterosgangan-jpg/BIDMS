using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BDIMS.Migrations
{
    /// <inheritdoc />
    public partial class AddNotificationPreferences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "NotifyAnnouncements",
                table: "UserAccounts",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "NotifyRequestUpdates",
                table: "UserAccounts",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "NotifyAnnouncements",
                table: "UserAccounts");

            migrationBuilder.DropColumn(
                name: "NotifyRequestUpdates",
                table: "UserAccounts");
        }
    }
}

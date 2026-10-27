using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BDIMS.Migrations
{
    /// <inheritdoc />
    public partial class AddUserAccountIdToDocumentRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "UserAccountId",
                table: "DocumentRequests",
                type: "int",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "UserAccountId",
                table: "DocumentRequests");
        }
    }
}

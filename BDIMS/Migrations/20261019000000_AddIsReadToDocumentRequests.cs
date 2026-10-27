using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BDIMS.Migrations
{
    /// <inheritdoc />
    public partial class AddIsReadToDocumentRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsRead",
                table: "DocumentRequests",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            // Requests that predate the sidebar badge are treated as already seen, so the
            // counter starts at zero instead of flagging the entire historical backlog.
            migrationBuilder.Sql("UPDATE `DocumentRequests` SET `IsRead` = 1;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsRead",
                table: "DocumentRequests");
        }
    }
}

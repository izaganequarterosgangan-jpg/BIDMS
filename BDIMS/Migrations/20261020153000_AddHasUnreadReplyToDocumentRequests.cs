using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BDIMS.Migrations
{
    /// <inheritdoc />
    public partial class AddHasUnreadReplyToDocumentRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "HasUnreadReply",
                table: "DocumentRequests",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            // Requests that predate the unread-reply dot have no pending reply to
            // acknowledge, so they start without the red badge.
            migrationBuilder.Sql("UPDATE `DocumentRequests` SET `HasUnreadReply` = 0;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HasUnreadReply",
                table: "DocumentRequests");
        }
    }
}

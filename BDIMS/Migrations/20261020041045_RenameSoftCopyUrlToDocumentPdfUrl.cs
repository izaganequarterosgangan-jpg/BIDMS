using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BDIMS.Migrations
{
    /// <inheritdoc />
    public partial class RenameSoftCopyUrlToDocumentPdfUrl : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SoftCopyNote",
                table: "DocumentRequests");

            migrationBuilder.RenameColumn(
                name: "SoftCopyUrl",
                table: "DocumentRequests",
                newName: "DocumentPdfUrl");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "DocumentPdfUrl",
                table: "DocumentRequests",
                newName: "SoftCopyUrl");

            migrationBuilder.AddColumn<string>(
                name: "SoftCopyNote",
                table: "DocumentRequests",
                type: "longtext",
                nullable: false)
                .Annotation("MySql:CharSet", "utf8mb4");
        }
    }
}

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BDIMS.Migrations
{
    /// <inheritdoc />
    public partial class AddDocumentPdfUrlToCertificates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DocumentPdfUrl",
                table: "Certificates",
                type: "longtext",
                nullable: false)
                .Annotation("MySql:CharSet", "utf8mb4");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DocumentPdfUrl",
                table: "Certificates");
        }
    }
}

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BDIMS.Migrations
{
    /// <inheritdoc />
    public partial class AddSoftCopyDelivery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SoftCopyNote",
                table: "DocumentRequests",
                type: "longtext",
                nullable: false)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "SoftCopyUrl",
                table: "DocumentRequests",
                type: "longtext",
                nullable: false)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "ClaimFormat",
                table: "Certificates",
                type: "longtext",
                nullable: false)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "RequestId",
                table: "Certificates",
                type: "longtext",
                nullable: false)
                .Annotation("MySql:CharSet", "utf8mb4");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SoftCopyNote",
                table: "DocumentRequests");

            migrationBuilder.DropColumn(
                name: "SoftCopyUrl",
                table: "DocumentRequests");

            migrationBuilder.DropColumn(
                name: "ClaimFormat",
                table: "Certificates");

            migrationBuilder.DropColumn(
                name: "RequestId",
                table: "Certificates");
        }
    }
}

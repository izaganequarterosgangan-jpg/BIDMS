using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BDIMS.Migrations
{
    /// <inheritdoc />
    public partial class AddResidentRegistrationFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Age",
                table: "UserAccounts",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Gender",
                table: "UserAccounts",
                type: "varchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "MiddleName",
                table: "UserAccounts",
                type: "varchar(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "Purok",
                table: "UserAccounts",
                type: "varchar(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "Suffix",
                table: "UserAccounts",
                type: "varchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "VoterStatus",
                table: "UserAccounts",
                type: "varchar(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "")
                .Annotation("MySql:CharSet", "utf8mb4");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Age",
                table: "UserAccounts");

            migrationBuilder.DropColumn(
                name: "Gender",
                table: "UserAccounts");

            migrationBuilder.DropColumn(
                name: "MiddleName",
                table: "UserAccounts");

            migrationBuilder.DropColumn(
                name: "Purok",
                table: "UserAccounts");

            migrationBuilder.DropColumn(
                name: "Suffix",
                table: "UserAccounts");

            migrationBuilder.DropColumn(
                name: "VoterStatus",
                table: "UserAccounts");
        }
    }
}

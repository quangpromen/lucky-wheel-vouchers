using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LuckyWheel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAdminWheelManagementConcurrency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_WheelVersionPrizes_ProbabilityWeight",
                table: "WheelVersionPrizes");

            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                table: "WheelVersionPrizes",
                type: "rowversion",
                rowVersion: true,
                nullable: false,
                defaultValue: new byte[0]);

            migrationBuilder.AddCheckConstraint(
                name: "CK_WheelVersionPrizes_ProbabilityWeight",
                table: "WheelVersionPrizes",
                sql: "[ProbabilityWeight] > 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_WheelVersionPrizes_ProbabilityWeight",
                table: "WheelVersionPrizes");

            migrationBuilder.DropColumn(
                name: "RowVersion",
                table: "WheelVersionPrizes");

            migrationBuilder.AddCheckConstraint(
                name: "CK_WheelVersionPrizes_ProbabilityWeight",
                table: "WheelVersionPrizes",
                sql: "[ProbabilityWeight] >= 0");
        }
    }
}

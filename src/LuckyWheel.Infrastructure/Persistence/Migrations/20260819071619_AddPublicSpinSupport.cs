using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LuckyWheel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPublicSpinSupport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "PrizeKeyId",
                table: "WinnerLocks",
                type: "uniqueidentifier",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier");

            migrationBuilder.AlterColumn<string>(
                name: "IdempotencyKey",
                table: "SpinHistories",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier");

            migrationBuilder.AddColumn<DateTime>(
                name: "AcceptedTermsAtUtc",
                table: "SpinHistories",
                type: "datetime2",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<Guid>(
                name: "WheelVersionPrizeId",
                table: "SpinHistories",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateIndex(
                name: "IX_SpinHistories_WheelVersionPrizeId_CreatedAtUtc",
                table: "SpinHistories",
                columns: new[] { "WheelVersionPrizeId", "CreatedAtUtc" });

            migrationBuilder.AddForeignKey(
                name: "FK_SpinHistories_WheelVersionPrizes_WheelVersionPrizeId",
                table: "SpinHistories",
                column: "WheelVersionPrizeId",
                principalTable: "WheelVersionPrizes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SpinHistories_WheelVersionPrizes_WheelVersionPrizeId",
                table: "SpinHistories");

            migrationBuilder.DropIndex(
                name: "IX_SpinHistories_WheelVersionPrizeId_CreatedAtUtc",
                table: "SpinHistories");

            migrationBuilder.DropColumn(
                name: "AcceptedTermsAtUtc",
                table: "SpinHistories");

            migrationBuilder.DropColumn(
                name: "WheelVersionPrizeId",
                table: "SpinHistories");

            migrationBuilder.AlterColumn<Guid>(
                name: "PrizeKeyId",
                table: "WinnerLocks",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "IdempotencyKey",
                table: "SpinHistories",
                type: "uniqueidentifier",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(100)",
                oldMaxLength: 100);
        }
    }
}

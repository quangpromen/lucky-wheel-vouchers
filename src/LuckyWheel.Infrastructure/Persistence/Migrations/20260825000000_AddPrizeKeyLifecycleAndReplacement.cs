using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LuckyWheel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPrizeKeyLifecycleAndReplacement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ReplacesPrizeKeyId",
                table: "PrizeKeys",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_PrizeKeys_ReplacesPrizeKeyId",
                table: "PrizeKeys",
                column: "ReplacesPrizeKeyId",
                unique: true,
                filter: "[ReplacesPrizeKeyId] IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_PrizeKeys_PrizeKeys_ReplacesPrizeKeyId",
                table: "PrizeKeys",
                column: "ReplacesPrizeKeyId",
                principalTable: "PrizeKeys",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PrizeKeys_PrizeKeys_ReplacesPrizeKeyId",
                table: "PrizeKeys");

            migrationBuilder.DropIndex(
                name: "IX_PrizeKeys_ReplacesPrizeKeyId",
                table: "PrizeKeys");

            migrationBuilder.DropColumn(
                name: "ReplacesPrizeKeyId",
                table: "PrizeKeys");
        }
    }
}

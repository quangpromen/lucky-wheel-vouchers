using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LuckyWheel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SplitPrizeKeyProtectionComponents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("IF EXISTS (SELECT 1 FROM [PrizeKeys]) THROW 51000, 'PrizeKey encryption storage migration requires an empty PrizeKeys table; migrate existing protected values through an approved key-aware process.', 1;");

            migrationBuilder.AddColumn<byte[]>(
                name: "EncryptedCode",
                table: "PrizeKeys",
                type: "varbinary(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "EncryptionNonce",
                table: "PrizeKeys",
                type: "varbinary(12)",
                maxLength: 12,
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "EncryptionTag",
                table: "PrizeKeys",
                type: "varbinary(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.DropColumn(name: "CodeEncrypted", table: "PrizeKeys");

            migrationBuilder.AlterColumn<byte[]>(name: "EncryptedCode", table: "PrizeKeys", type: "varbinary(64)", maxLength: 64, nullable: false, oldClrType: typeof(byte[]), oldType: "varbinary(64)", oldMaxLength: 64, oldNullable: true);
            migrationBuilder.AlterColumn<byte[]>(name: "EncryptionNonce", table: "PrizeKeys", type: "varbinary(12)", maxLength: 12, nullable: false, oldClrType: typeof(byte[]), oldType: "varbinary(12)", oldMaxLength: 12, oldNullable: true);
            migrationBuilder.AlterColumn<byte[]>(name: "EncryptionTag", table: "PrizeKeys", type: "varbinary(16)", maxLength: 16, nullable: false, oldClrType: typeof(byte[]), oldType: "varbinary(16)", oldMaxLength: 16, oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("IF EXISTS (SELECT 1 FROM [PrizeKeys]) THROW 51000, 'PrizeKey encryption storage downgrade requires an empty PrizeKeys table.', 1;");

            migrationBuilder.AddColumn<string>(name: "CodeEncrypted", table: "PrizeKeys", type: "nvarchar(1000)", maxLength: 1000, nullable: true);
            migrationBuilder.DropColumn(
                name: "EncryptedCode",
                table: "PrizeKeys");

            migrationBuilder.DropColumn(
                name: "EncryptionNonce",
                table: "PrizeKeys");

            migrationBuilder.DropColumn(
                name: "EncryptionTag",
                table: "PrizeKeys");

            migrationBuilder.AlterColumn<string>(name: "CodeEncrypted", table: "PrizeKeys", type: "nvarchar(1000)", maxLength: 1000, nullable: false, oldClrType: typeof(string), oldType: "nvarchar(1000)", oldMaxLength: 1000, oldNullable: true);
        }
    }
}

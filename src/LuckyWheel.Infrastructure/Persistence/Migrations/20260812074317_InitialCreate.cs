using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LuckyWheel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AdminUsers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Email = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdminUsers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Wheels",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Slug = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Terms = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Wheels", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AuditLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AdminUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Action = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    EntityType = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    EntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Description = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    MetadataJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AuditLogs_AdminUsers_AdminUserId",
                        column: x => x.AdminUserId,
                        principalTable: "AdminUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Prizes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WheelId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ImageUrl = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: true),
                    RequiresKey = table.Column<bool>(type: "bit", nullable: false),
                    TotalQuantity = table.Column<int>(type: "int", nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Prizes", x => x.Id);
                    table.CheckConstraint("CK_Prizes_KeyRequiresQuantity", "[RequiresKey] = 0 OR [TotalQuantity] > 0");
                    table.CheckConstraint("CK_Prizes_TotalQuantity", "[TotalQuantity] >= 0");
                    table.ForeignKey(
                        name: "FK_Prizes_Wheels_WheelId",
                        column: x => x.WheelId,
                        principalTable: "Wheels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WheelVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WheelId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VersionNumber = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    StartAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    EndAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ClaimDurationMinutes = table.Column<int>(type: "int", nullable: false),
                    PublishedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    PublishedByAdminId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ClosedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WheelVersions", x => x.Id);
                    table.CheckConstraint("CK_WheelVersions_ClaimDuration", "[ClaimDurationMinutes] > 0");
                    table.CheckConstraint("CK_WheelVersions_ValidPeriod", "[EndAtUtc] > [StartAtUtc]");
                    table.CheckConstraint("CK_WheelVersions_VersionNumber", "[VersionNumber] > 0");
                    table.ForeignKey(
                        name: "FK_WheelVersions_Wheels_WheelId",
                        column: x => x.WheelId,
                        principalTable: "Wheels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WheelVersionPrizes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WheelVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PrizeId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ProbabilityWeight = table.Column<int>(type: "int", nullable: false),
                    DisplayOrder = table.Column<int>(type: "int", nullable: false),
                    Color = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    ImageUrl = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: true),
                    IsNoPrize = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WheelVersionPrizes", x => x.Id);
                    table.CheckConstraint("CK_WheelVersionPrizes_DisplayOrder", "[DisplayOrder] > 0");
                    table.CheckConstraint("CK_WheelVersionPrizes_PrizeReference", "([IsNoPrize] = 1 AND [PrizeId] IS NULL) OR ([IsNoPrize] = 0 AND [PrizeId] IS NOT NULL)");
                    table.CheckConstraint("CK_WheelVersionPrizes_ProbabilityWeight", "[ProbabilityWeight] >= 0");
                    table.ForeignKey(
                        name: "FK_WheelVersionPrizes_Prizes_PrizeId",
                        column: x => x.PrizeId,
                        principalTable: "Prizes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WheelVersionPrizes_WheelVersions_WheelVersionId",
                        column: x => x.WheelVersionId,
                        principalTable: "WheelVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PrizeKeys",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PrizeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CodeHash = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    CodeEncrypted = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    AssignedSpinId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AssignedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ExpiresAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RedeemedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ExpiredAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CancelledAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PrizeKeys", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PrizeKeys_Prizes_PrizeId",
                        column: x => x.PrizeId,
                        principalTable: "Prizes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SpinHistories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WheelId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WheelVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EmailOriginal = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    EmailNormalized = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    Result = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    PrizeId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PrizeKeyId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IdempotencyKey = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReceiptToken = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CancelledAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CancelledByAdminId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CancellationReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SpinHistories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SpinHistories_PrizeKeys_PrizeKeyId",
                        column: x => x.PrizeKeyId,
                        principalTable: "PrizeKeys",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SpinHistories_Prizes_PrizeId",
                        column: x => x.PrizeId,
                        principalTable: "Prizes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SpinHistories_WheelVersions_WheelVersionId",
                        column: x => x.WheelVersionId,
                        principalTable: "WheelVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SpinHistories_Wheels_WheelId",
                        column: x => x.WheelId,
                        principalTable: "Wheels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PrizeRedemptions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SpinId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PrizeKeyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConfirmedByAdminId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConfirmedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Note = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PrizeRedemptions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PrizeRedemptions_AdminUsers_ConfirmedByAdminId",
                        column: x => x.ConfirmedByAdminId,
                        principalTable: "AdminUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PrizeRedemptions_PrizeKeys_PrizeKeyId",
                        column: x => x.PrizeKeyId,
                        principalTable: "PrizeKeys",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PrizeRedemptions_SpinHistories_SpinId",
                        column: x => x.SpinId,
                        principalTable: "SpinHistories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WinnerLocks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WheelId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EmailNormalized = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    SpinId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PrizeKeyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    IsBlocked = table.Column<bool>(type: "bit", nullable: false),
                    LockedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UnlockedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    UnlockedByAdminId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    BlockReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WinnerLocks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WinnerLocks_PrizeKeys_PrizeKeyId",
                        column: x => x.PrizeKeyId,
                        principalTable: "PrizeKeys",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WinnerLocks_SpinHistories_SpinId",
                        column: x => x.SpinId,
                        principalTable: "SpinHistories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WinnerLocks_Wheels_WheelId",
                        column: x => x.WheelId,
                        principalTable: "Wheels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AdminUsers_Email",
                table: "AdminUsers",
                column: "Email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_Action_CreatedAtUtc",
                table: "AuditLogs",
                columns: new[] { "Action", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_AdminUserId_CreatedAtUtc",
                table: "AuditLogs",
                columns: new[] { "AdminUserId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_EntityType_EntityId_CreatedAtUtc",
                table: "AuditLogs",
                columns: new[] { "EntityType", "EntityId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PrizeKeys_AssignedSpinId",
                table: "PrizeKeys",
                column: "AssignedSpinId");

            migrationBuilder.CreateIndex(
                name: "IX_PrizeKeys_CodeHash",
                table: "PrizeKeys",
                column: "CodeHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PrizeKeys_PrizeId_Status_CreatedAtUtc",
                table: "PrizeKeys",
                columns: new[] { "PrizeId", "Status", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PrizeKeys_Status_ExpiresAtUtc",
                table: "PrizeKeys",
                columns: new[] { "Status", "ExpiresAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PrizeRedemptions_ConfirmedByAdminId",
                table: "PrizeRedemptions",
                column: "ConfirmedByAdminId");

            migrationBuilder.CreateIndex(
                name: "IX_PrizeRedemptions_PrizeKeyId",
                table: "PrizeRedemptions",
                column: "PrizeKeyId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PrizeRedemptions_SpinId",
                table: "PrizeRedemptions",
                column: "SpinId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Prizes_WheelId",
                table: "Prizes",
                column: "WheelId");

            migrationBuilder.CreateIndex(
                name: "IX_SpinHistories_IdempotencyKey",
                table: "SpinHistories",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SpinHistories_PrizeId_CreatedAtUtc",
                table: "SpinHistories",
                columns: new[] { "PrizeId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_SpinHistories_PrizeKeyId",
                table: "SpinHistories",
                column: "PrizeKeyId",
                unique: true,
                filter: "[PrizeKeyId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_SpinHistories_ReceiptToken",
                table: "SpinHistories",
                column: "ReceiptToken",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SpinHistories_WheelId_EmailNormalized_CreatedAtUtc",
                table: "SpinHistories",
                columns: new[] { "WheelId", "EmailNormalized", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_SpinHistories_WheelVersionId_CreatedAtUtc",
                table: "SpinHistories",
                columns: new[] { "WheelVersionId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Wheels_Slug",
                table: "Wheels",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WheelVersionPrizes_PrizeId",
                table: "WheelVersionPrizes",
                column: "PrizeId");

            migrationBuilder.CreateIndex(
                name: "IX_WheelVersionPrizes_WheelVersionId_DisplayOrder",
                table: "WheelVersionPrizes",
                columns: new[] { "WheelVersionId", "DisplayOrder" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WheelVersionPrizes_WheelVersionId_PrizeId",
                table: "WheelVersionPrizes",
                columns: new[] { "WheelVersionId", "PrizeId" });

            migrationBuilder.CreateIndex(
                name: "IX_WheelVersions_WheelId",
                table: "WheelVersions",
                column: "WheelId",
                unique: true,
                filter: "[Status] = 'Active'");

            migrationBuilder.CreateIndex(
                name: "IX_WheelVersions_WheelId_VersionNumber",
                table: "WheelVersions",
                columns: new[] { "WheelId", "VersionNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WinnerLocks_PrizeKeyId",
                table: "WinnerLocks",
                column: "PrizeKeyId");

            migrationBuilder.CreateIndex(
                name: "IX_WinnerLocks_SpinId",
                table: "WinnerLocks",
                column: "SpinId");

            migrationBuilder.CreateIndex(
                name: "IX_WinnerLocks_WheelId_EmailNormalized",
                table: "WinnerLocks",
                columns: new[] { "WheelId", "EmailNormalized" },
                unique: true,
                filter: "[IsActive] = 1");

            migrationBuilder.AddForeignKey(
                name: "FK_PrizeKeys_SpinHistories_AssignedSpinId",
                table: "PrizeKeys",
                column: "AssignedSpinId",
                principalTable: "SpinHistories",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PrizeKeys_Prizes_PrizeId",
                table: "PrizeKeys");

            migrationBuilder.DropForeignKey(
                name: "FK_SpinHistories_Prizes_PrizeId",
                table: "SpinHistories");

            migrationBuilder.DropForeignKey(
                name: "FK_PrizeKeys_SpinHistories_AssignedSpinId",
                table: "PrizeKeys");

            migrationBuilder.DropTable(
                name: "AuditLogs");

            migrationBuilder.DropTable(
                name: "PrizeRedemptions");

            migrationBuilder.DropTable(
                name: "WheelVersionPrizes");

            migrationBuilder.DropTable(
                name: "WinnerLocks");

            migrationBuilder.DropTable(
                name: "AdminUsers");

            migrationBuilder.DropTable(
                name: "Prizes");

            migrationBuilder.DropTable(
                name: "SpinHistories");

            migrationBuilder.DropTable(
                name: "PrizeKeys");

            migrationBuilder.DropTable(
                name: "WheelVersions");

            migrationBuilder.DropTable(
                name: "Wheels");
        }
    }
}

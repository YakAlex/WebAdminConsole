using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AdminConsole.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AppLogEntries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Severity = table.Column<int>(type: "INTEGER", nullable: false),
                    Source = table.Column<string>(type: "TEXT", nullable: false),
                    Message = table.Column<string>(type: "TEXT", nullable: false),
                    Timestamp = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppLogEntries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AppSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RdpMonitoringEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    ZabbixMonitoringEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    BackupMonitoringEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    TelegramPrimaryAdminChatId = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BackupCheckStates",
                columns: table => new
                {
                    BackupCheckStateId = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Host = table.Column<string>(type: "TEXT", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Outcome = table.Column<int>(type: "INTEGER", nullable: false),
                    LastConfirmedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LastConfirmedOutcome = table.Column<int>(type: "INTEGER", nullable: true),
                    ConsecutiveUnknownCount = table.Column<int>(type: "INTEGER", nullable: false),
                    ConsecutiveBadCount = table.Column<int>(type: "INTEGER", nullable: false),
                    LastRawOutcome = table.Column<int>(type: "INTEGER", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BackupCheckStates", x => x.BackupCheckStateId);
                });

            migrationBuilder.CreateTable(
                name: "DowntimeRecords",
                columns: table => new
                {
                    ServerIp = table.Column<string>(type: "TEXT", nullable: false),
                    FellAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ServerName = table.Column<string>(type: "TEXT", nullable: false),
                    ServerGroup = table.Column<string>(type: "TEXT", nullable: false),
                    RecoveredAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ClosedByMaintenance = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DowntimeRecords", x => new { x.ServerIp, x.FellAt });
                });

            migrationBuilder.CreateTable(
                name: "MaintenanceWindows",
                columns: table => new
                {
                    WindowKey = table.Column<string>(type: "TEXT", nullable: false),
                    ServerIp = table.Column<string>(type: "TEXT", nullable: true),
                    TargetGroup = table.Column<string>(type: "TEXT", nullable: true),
                    DisplayName = table.Column<string>(type: "TEXT", nullable: false),
                    From = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    To = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    Reason = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MaintenanceWindows", x => x.WindowKey);
                });

            migrationBuilder.CreateTable(
                name: "MigrationMarker",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MigrationMarker", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TelegramAllowedUsers",
                columns: table => new
                {
                    ChatId = table.Column<long>(type: "INTEGER", nullable: false),
                    Username = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TelegramAllowedUsers", x => x.ChatId);
                });

            migrationBuilder.CreateTable(
                name: "BackupSamples",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ObservedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    BackupCheckStateId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BackupSamples", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BackupSamples_BackupCheckStates_BackupCheckStateId",
                        column: x => x.BackupCheckStateId,
                        principalTable: "BackupCheckStates",
                        principalColumn: "BackupCheckStateId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AppLogEntries_Timestamp",
                table: "AppLogEntries",
                column: "Timestamp");

            migrationBuilder.CreateIndex(
                name: "IX_BackupCheckStates_Name_Kind",
                table: "BackupCheckStates",
                columns: new[] { "Name", "Kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BackupSamples_BackupCheckStateId",
                table: "BackupSamples",
                column: "BackupCheckStateId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AppLogEntries");

            migrationBuilder.DropTable(
                name: "AppSettings");

            migrationBuilder.DropTable(
                name: "BackupSamples");

            migrationBuilder.DropTable(
                name: "DowntimeRecords");

            migrationBuilder.DropTable(
                name: "MaintenanceWindows");

            migrationBuilder.DropTable(
                name: "MigrationMarker");

            migrationBuilder.DropTable(
                name: "TelegramAllowedUsers");

            migrationBuilder.DropTable(
                name: "BackupCheckStates");
        }
    }
}

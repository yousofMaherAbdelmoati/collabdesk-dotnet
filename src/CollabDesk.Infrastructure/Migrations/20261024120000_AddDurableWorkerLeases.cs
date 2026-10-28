using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CollabDesk.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDurableWorkerLeases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LeaseExpiresAt",
                table: "ReportJobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "LeaseId",
                table: "ReportJobs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "NextAttemptAt",
                table: "ReportJobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Attempts",
                table: "Outbox",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "LastError",
                table: "Outbox",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LeaseExpiresAt",
                table: "Outbox",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "LeaseId",
                table: "Outbox",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "NextAttemptAt",
                table: "Outbox",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Status",
                table: "Outbox",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_ReportJobs_Status_NextAttemptAt_Id",
                table: "ReportJobs",
                columns: new[] { "Status", "NextAttemptAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Outbox_Status_NextAttemptAt_Id",
                table: "Outbox",
                columns: new[] { "Status", "NextAttemptAt", "Id" });

            // A legacy record with an acknowledgement must not be replayed merely because the
            // status column did not exist when it was originally written.
            migrationBuilder.Sql("UPDATE \"Outbox\" SET \"Status\" = 2 WHERE \"PublishedAt\" IS NOT NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ReportJobs_Status_NextAttemptAt_Id",
                table: "ReportJobs");

            migrationBuilder.DropIndex(
                name: "IX_Outbox_Status_NextAttemptAt_Id",
                table: "Outbox");

            migrationBuilder.DropColumn(
                name: "LeaseExpiresAt",
                table: "ReportJobs");

            migrationBuilder.DropColumn(
                name: "LeaseId",
                table: "ReportJobs");

            migrationBuilder.DropColumn(
                name: "NextAttemptAt",
                table: "ReportJobs");

            migrationBuilder.DropColumn(
                name: "Attempts",
                table: "Outbox");

            migrationBuilder.DropColumn(
                name: "LastError",
                table: "Outbox");

            migrationBuilder.DropColumn(
                name: "LeaseExpiresAt",
                table: "Outbox");

            migrationBuilder.DropColumn(
                name: "LeaseId",
                table: "Outbox");

            migrationBuilder.DropColumn(
                name: "NextAttemptAt",
                table: "Outbox");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "Outbox");
        }
    }
}

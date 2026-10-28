using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace CollabDesk.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddReportResultsAndAggregates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ReportJobs_Fingerprint_Status",
                table: "ReportJobs");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AssignedAt",
                table: "Tickets",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ResolvedAt",
                table: "Tickets",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CompletedAt",
                table: "ReportJobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RequestJson",
                table: "ReportJobs",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ResultExpiresAt",
                table: "ReportJobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "DailyAgentStats",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Day = table.Column<DateOnly>(type: "date", nullable: false),
                    AgentId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClaimedTickets = table.Column<int>(type: "integer", nullable: false),
                    ResolvedTickets = table.Column<int>(type: "integer", nullable: false),
                    ResponseTimeTotalSeconds = table.Column<long>(type: "bigint", nullable: false),
                    ResponseTimeCount = table.Column<int>(type: "integer", nullable: false),
                    ActivityCount = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DailyAgentStats", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DailyTicketStats",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Day = table.Column<DateOnly>(type: "date", nullable: false),
                    CreatedTickets = table.Column<int>(type: "integer", nullable: false),
                    ClaimedTickets = table.Column<int>(type: "integer", nullable: false),
                    ResolvedTickets = table.Column<int>(type: "integer", nullable: false),
                    ResponseTimeTotalSeconds = table.Column<long>(type: "bigint", nullable: false),
                    ResponseTimeCount = table.Column<int>(type: "integer", nullable: false),
                    ActivityCount = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DailyTicketStats", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MonthlyAgentStats",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Month = table.Column<DateOnly>(type: "date", nullable: false),
                    AgentId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClaimedTickets = table.Column<int>(type: "integer", nullable: false),
                    ResolvedTickets = table.Column<int>(type: "integer", nullable: false),
                    ResponseTimeTotalSeconds = table.Column<long>(type: "bigint", nullable: false),
                    ResponseTimeCount = table.Column<int>(type: "integer", nullable: false),
                    ActivityCount = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MonthlyAgentStats", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MonthlyTicketStats",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Month = table.Column<DateOnly>(type: "date", nullable: false),
                    CreatedTickets = table.Column<int>(type: "integer", nullable: false),
                    ClaimedTickets = table.Column<int>(type: "integer", nullable: false),
                    ResolvedTickets = table.Column<int>(type: "integer", nullable: false),
                    ResponseTimeTotalSeconds = table.Column<long>(type: "bigint", nullable: false),
                    ResponseTimeCount = table.Column<int>(type: "integer", nullable: false),
                    ActivityCount = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MonthlyTicketStats", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ReportJobs_Fingerprint",
                table: "ReportJobs",
                column: "Fingerprint",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DailyAgentStats_Day_AgentId",
                table: "DailyAgentStats",
                columns: new[] { "Day", "AgentId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DailyTicketStats_Day",
                table: "DailyTicketStats",
                column: "Day",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MonthlyAgentStats_Month_AgentId",
                table: "MonthlyAgentStats",
                columns: new[] { "Month", "AgentId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MonthlyTicketStats_Month",
                table: "MonthlyTicketStats",
                column: "Month",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DailyAgentStats");

            migrationBuilder.DropTable(
                name: "DailyTicketStats");

            migrationBuilder.DropTable(
                name: "MonthlyAgentStats");

            migrationBuilder.DropTable(
                name: "MonthlyTicketStats");

            migrationBuilder.DropIndex(
                name: "IX_ReportJobs_Fingerprint",
                table: "ReportJobs");

            migrationBuilder.DropColumn(
                name: "AssignedAt",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "ResolvedAt",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "CompletedAt",
                table: "ReportJobs");

            migrationBuilder.DropColumn(
                name: "RequestJson",
                table: "ReportJobs");

            migrationBuilder.DropColumn(
                name: "ResultExpiresAt",
                table: "ReportJobs");

            migrationBuilder.CreateIndex(
                name: "IX_ReportJobs_Fingerprint_Status",
                table: "ReportJobs",
                columns: new[] { "Fingerprint", "Status" });
        }
    }
}

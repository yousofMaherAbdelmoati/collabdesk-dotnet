using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CollabDesk.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTicketContractIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Title",
                table: "Tickets",
                type: "character varying(160)",
                maxLength: 160,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AlterColumn<string>(
                name: "Description",
                table: "Tickets",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AlterColumn<string>(
                name: "Body",
                table: "Messages",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AlterColumn<string>(
                name: "Key",
                table: "IdempotencyRecords",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_CustomerId_CreatedAt_Id",
                table: "Tickets",
                columns: new[] { "CustomerId", "CreatedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_Status_AssignedAgentId_CreatedAt_Id",
                table: "Tickets",
                columns: new[] { "Status", "AssignedAgentId", "CreatedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Messages_TicketId_CreatedAt_Id",
                table: "Messages",
                columns: new[] { "TicketId", "CreatedAt", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Tickets_CustomerId_CreatedAt_Id",
                table: "Tickets");

            migrationBuilder.DropIndex(
                name: "IX_Tickets_Status_AssignedAgentId_CreatedAt_Id",
                table: "Tickets");

            migrationBuilder.DropIndex(
                name: "IX_Messages_TicketId_CreatedAt_Id",
                table: "Messages");

            migrationBuilder.AlterColumn<string>(
                name: "Title",
                table: "Tickets",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(160)",
                oldMaxLength: 160);

            migrationBuilder.AlterColumn<string>(
                name: "Description",
                table: "Tickets",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(4000)",
                oldMaxLength: 4000);

            migrationBuilder.AlterColumn<string>(
                name: "Body",
                table: "Messages",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(4000)",
                oldMaxLength: 4000);

            migrationBuilder.AlterColumn<string>(
                name: "Key",
                table: "IdempotencyRecords",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(200)",
                oldMaxLength: 200);
        }
    }
}

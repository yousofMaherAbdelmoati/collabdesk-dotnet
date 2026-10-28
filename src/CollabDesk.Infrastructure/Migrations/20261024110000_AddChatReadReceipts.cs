using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace CollabDesk.Infrastructure.Migrations;

[DbContext(typeof(CollabDeskDbContext))]
[Migration("20261024110000_AddChatReadReceipts")]
public partial class AddChatReadReceipts : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "ChatReadReceipts",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                TicketId = table.Column<long>(type: "bigint", nullable: false),
                ReaderId = table.Column<Guid>(type: "uuid", nullable: false),
                LastReadMessageId = table.Column<long>(type: "bigint", nullable: false),
                ReadAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_ChatReadReceipts", x => x.Id));

        migrationBuilder.CreateIndex(
            name: "IX_ChatReadReceipts_TicketId_ReaderId",
            table: "ChatReadReceipts",
            columns: new[] { "TicketId", "ReaderId" },
            unique: true);
        migrationBuilder.CreateIndex(
            name: "IX_ChatReadReceipts_TicketId_LastReadMessageId",
            table: "ChatReadReceipts",
            columns: new[] { "TicketId", "LastReadMessageId" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "ChatReadReceipts");
    }
}

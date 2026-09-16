using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DuelMasters.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddMatchRecords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MatchRecords",
                columns: table => new
                {
                    Code = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    HostUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    HostName = table.Column<string>(type: "text", nullable: false),
                    HostDeckId = table.Column<Guid>(type: "uuid", nullable: true),
                    JoinerUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    JoinerName = table.Column<string>(type: "text", nullable: false),
                    JoinerDeckId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FinishedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    WinnerSide = table.Column<string>(type: "text", nullable: true),
                    IsRanked = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MatchRecords", x => x.Code);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MatchRecords_CreatedAtUtc",
                table: "MatchRecords",
                column: "CreatedAtUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MatchRecords");
        }
    }
}

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TailControl.Hub.Data.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AuditLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TimeUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    ClientId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ClientLabel = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    RemoteIp = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Action = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Target = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Result = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Detail = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditLog", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Devices",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false, collation: "NOCASE"),
                    MacAddress = table.Column<string>(type: "TEXT", maxLength: 17, nullable: true),
                    Host = table.Column<string>(type: "TEXT", maxLength: 253, nullable: false),
                    AgentPort = table.Column<int>(type: "INTEGER", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    CreatedUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    LastSeenUtc = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Devices", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuditLog_TimeUtc",
                table: "AuditLog",
                column: "TimeUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Devices_Host",
                table: "Devices",
                column: "Host",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Devices_Name",
                table: "Devices",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuditLog");

            migrationBuilder.DropTable(
                name: "Devices");
        }
    }
}

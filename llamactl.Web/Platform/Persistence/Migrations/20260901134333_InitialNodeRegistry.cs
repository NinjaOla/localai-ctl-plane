using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace llamactl.Web.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialNodeRegistry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Nodes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    BootstrapTokenHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Health = table.Column<int>(type: "INTEGER", nullable: false),
                    LastSeen = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    GpuName = table.Column<string>(type: "TEXT", nullable: true),
                    VramTotalMiB = table.Column<long>(type: "INTEGER", nullable: true),
                    LlamaCppVersion = table.Column<string>(type: "TEXT", nullable: true),
                    RocmVersion = table.Column<string>(type: "TEXT", nullable: true),
                    Version = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Nodes", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Nodes_Name",
                table: "Nodes",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Nodes");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace llamactl.Web.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDesiredStateReconciliation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ConfigurationJson",
                table: "Nodes",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "DesiredStateVersion",
                table: "Nodes",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "ValidationIssuesJson",
                table: "Nodes",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Instances",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    NodeId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    SpecJson = table.Column<string>(type: "TEXT", nullable: false),
                    DesiredState = table.Column<int>(type: "INTEGER", nullable: false),
                    ObservedState = table.Column<int>(type: "INTEGER", nullable: false),
                    ProcessId = table.Column<int>(type: "INTEGER", nullable: true),
                    AdoptProcessId = table.Column<int>(type: "INTEGER", nullable: true),
                    Error = table.Column<string>(type: "TEXT", nullable: true),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Instances", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Instances_Nodes_NodeId",
                        column: x => x.NodeId,
                        principalTable: "Nodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Instances_NodeId_Name",
                table: "Instances",
                columns: new[] { "NodeId", "Name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Instances");

            migrationBuilder.DropColumn(
                name: "ConfigurationJson",
                table: "Nodes");

            migrationBuilder.DropColumn(
                name: "DesiredStateVersion",
                table: "Nodes");

            migrationBuilder.DropColumn(
                name: "ValidationIssuesJson",
                table: "Nodes");
        }
    }
}

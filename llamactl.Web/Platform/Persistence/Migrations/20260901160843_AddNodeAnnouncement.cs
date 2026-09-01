using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace llamactl.Web.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddNodeAnnouncement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AnnouncementJson",
                table: "Nodes",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AnnouncementJson",
                table: "Nodes");
        }
    }
}

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace portaBLe.Migrations
{
    /// <inheritdoc />
    public partial class AddOutlierCountToLeaderboard : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "OutlierCount",
                table: "Leaderboards",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OutlierCount",
                table: "Leaderboards");
        }
    }
}

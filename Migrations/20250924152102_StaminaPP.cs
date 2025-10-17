using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace portaBLe.Migrations
{
    /// <inheritdoc />
    public partial class StaminaPP : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<float>(
                name: "StaminaPP",
                table: "Scores",
                type: "REAL",
                nullable: false,
                defaultValue: 0f);

            migrationBuilder.AddColumn<float>(
                name: "StaminaPp",
                table: "Players",
                type: "REAL",
                nullable: false,
                defaultValue: 0f);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "StaminaPP",
                table: "Scores");

            migrationBuilder.DropColumn(
                name: "StaminaPp",
                table: "Players");
        }
    }
}

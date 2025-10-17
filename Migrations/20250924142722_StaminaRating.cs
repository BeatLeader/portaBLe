using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace portaBLe.Migrations
{
    /// <inheritdoc />
    public partial class StaminaRating : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<float>(
                name: "FSStaminaRating",
                table: "ModifiersRating",
                type: "REAL",
                nullable: false,
                defaultValue: 0f);

            migrationBuilder.AddColumn<float>(
                name: "SFStaminaRating",
                table: "ModifiersRating",
                type: "REAL",
                nullable: false,
                defaultValue: 0f);

            migrationBuilder.AddColumn<float>(
                name: "SSStaminaRating",
                table: "ModifiersRating",
                type: "REAL",
                nullable: false,
                defaultValue: 0f);

            migrationBuilder.AddColumn<float>(
                name: "StaminaRating",
                table: "Leaderboards",
                type: "REAL",
                nullable: false,
                defaultValue: 0f);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FSStaminaRating",
                table: "ModifiersRating");

            migrationBuilder.DropColumn(
                name: "SFStaminaRating",
                table: "ModifiersRating");

            migrationBuilder.DropColumn(
                name: "SSStaminaRating",
                table: "ModifiersRating");

            migrationBuilder.DropColumn(
                name: "StaminaRating",
                table: "Leaderboards");
        }
    }
}

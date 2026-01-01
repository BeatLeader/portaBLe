using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace portaBLe.Migrations
{
    /// <inheritdoc />
    public partial class AddStatsTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Stats",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ModeName = table.Column<string>(type: "TEXT", nullable: true),
                    TotalOutlier = table.Column<int>(type: "INTEGER", nullable: false),
                    AvgOutlierPercentage = table.Column<float>(type: "REAL", nullable: false),
                    AvgMegametric = table.Column<float>(type: "REAL", nullable: false),
                    AvgMegametric40 = table.Column<float>(type: "REAL", nullable: false),
                    AvgMegametric75 = table.Column<float>(type: "REAL", nullable: false),
                    AvgMegametric125 = table.Column<float>(type: "REAL", nullable: false),
                    PpCount600 = table.Column<int>(type: "INTEGER", nullable: false),
                    PpCount700 = table.Column<int>(type: "INTEGER", nullable: false),
                    PpCount800 = table.Column<int>(type: "INTEGER", nullable: false),
                    PpCount900 = table.Column<int>(type: "INTEGER", nullable: false),
                    PpCount1000 = table.Column<int>(type: "INTEGER", nullable: false),
                    HighestStarRating = table.Column<float>(type: "REAL", nullable: false),
                    HighestAccRating = table.Column<float>(type: "REAL", nullable: false),
                    HighestPassRating = table.Column<float>(type: "REAL", nullable: false),
                    HighestTechRating = table.Column<float>(type: "REAL", nullable: false),
                    Top1PP = table.Column<float>(type: "REAL", nullable: false),
                    Top10PP = table.Column<float>(type: "REAL", nullable: false),
                    Top100PP = table.Column<float>(type: "REAL", nullable: false),
                    Top1000PP = table.Column<float>(type: "REAL", nullable: false),
                    Top2000PP = table.Column<float>(type: "REAL", nullable: false),
                    Top5000PP = table.Column<float>(type: "REAL", nullable: false),
                    Top10000PP = table.Column<float>(type: "REAL", nullable: false),
                    Top1AccPP = table.Column<float>(type: "REAL", nullable: false),
                    Top10AccPP = table.Column<float>(type: "REAL", nullable: false),
                    Top100AccPP = table.Column<float>(type: "REAL", nullable: false),
                    Top1000AccPP = table.Column<float>(type: "REAL", nullable: false),
                    Top2000AccPP = table.Column<float>(type: "REAL", nullable: false),
                    Top5000AccPP = table.Column<float>(type: "REAL", nullable: false),
                    Top10000AccPP = table.Column<float>(type: "REAL", nullable: false),
                    Top1PassPP = table.Column<float>(type: "REAL", nullable: false),
                    Top10PassPP = table.Column<float>(type: "REAL", nullable: false),
                    Top100PassPP = table.Column<float>(type: "REAL", nullable: false),
                    Top1000PassPP = table.Column<float>(type: "REAL", nullable: false),
                    Top2000PassPP = table.Column<float>(type: "REAL", nullable: false),
                    Top5000PassPP = table.Column<float>(type: "REAL", nullable: false),
                    Top10000PassPP = table.Column<float>(type: "REAL", nullable: false),
                    Top1TechPP = table.Column<float>(type: "REAL", nullable: false),
                    Top10TechPP = table.Column<float>(type: "REAL", nullable: false),
                    Top100TechPP = table.Column<float>(type: "REAL", nullable: false),
                    Top1000TechPP = table.Column<float>(type: "REAL", nullable: false),
                    Top2000TechPP = table.Column<float>(type: "REAL", nullable: false),
                    Top5000TechPP = table.Column<float>(type: "REAL", nullable: false),
                    Top10000TechPP = table.Column<float>(type: "REAL", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Stats", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Stats");
        }
    }
}

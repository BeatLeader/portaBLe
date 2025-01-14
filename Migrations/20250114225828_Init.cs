using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace portaBLe.Migrations
{
    /// <inheritdoc />
    public partial class Init : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ModifiersRating",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    FSPredictedAcc = table.Column<float>(type: "REAL", nullable: false),
                    FSPassRating = table.Column<float>(type: "REAL", nullable: false),
                    FSAccRating = table.Column<float>(type: "REAL", nullable: false),
                    FSTechRating = table.Column<float>(type: "REAL", nullable: false),
                    FSStars = table.Column<float>(type: "REAL", nullable: false),
                    SSPredictedAcc = table.Column<float>(type: "REAL", nullable: false),
                    SSPassRating = table.Column<float>(type: "REAL", nullable: false),
                    SSAccRating = table.Column<float>(type: "REAL", nullable: false),
                    SSTechRating = table.Column<float>(type: "REAL", nullable: false),
                    SSStars = table.Column<float>(type: "REAL", nullable: false),
                    SFPredictedAcc = table.Column<float>(type: "REAL", nullable: false),
                    SFPassRating = table.Column<float>(type: "REAL", nullable: false),
                    SFAccRating = table.Column<float>(type: "REAL", nullable: false),
                    SFTechRating = table.Column<float>(type: "REAL", nullable: false),
                    SFStars = table.Column<float>(type: "REAL", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ModifiersRating", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Players",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 25, nullable: false),
                    Pp = table.Column<float>(type: "REAL", nullable: false),
                    AccPp = table.Column<float>(type: "REAL", nullable: false),
                    TechPp = table.Column<float>(type: "REAL", nullable: false),
                    PassPp = table.Column<float>(type: "REAL", nullable: false),
                    Rank = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    Country = table.Column<string>(type: "TEXT", maxLength: 5, nullable: false),
                    CountryRank = table.Column<int>(type: "INTEGER", nullable: false),
                    Avatar = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    TopPp = table.Column<float>(type: "REAL", nullable: false),
                    RankedPlayCount = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Players", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Leaderboards",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 25, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 150, nullable: false),
                    Hash = table.Column<string>(type: "TEXT", nullable: false),
                    SongId = table.Column<string>(type: "TEXT", maxLength: 25, nullable: false),
                    ModeName = table.Column<string>(type: "TEXT", maxLength: 25, nullable: false),
                    DifficultyName = table.Column<string>(type: "TEXT", maxLength: 25, nullable: false),
                    Cover = table.Column<string>(type: "TEXT", maxLength: 150, nullable: false),
                    Mapper = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Stars = table.Column<float>(type: "REAL", nullable: false),
                    PassRating = table.Column<float>(type: "REAL", nullable: false),
                    AccRating = table.Column<float>(type: "REAL", nullable: false),
                    TechRating = table.Column<float>(type: "REAL", nullable: false),
                    PredictedAcc = table.Column<float>(type: "REAL", nullable: false),
                    ModifiersRatingId = table.Column<int>(type: "INTEGER", nullable: true),
                    Count = table.Column<int>(type: "INTEGER", nullable: false),
                    Count80 = table.Column<int>(type: "INTEGER", nullable: false),
                    Count95 = table.Column<int>(type: "INTEGER", nullable: false),
                    Average = table.Column<float>(type: "REAL", nullable: false),
                    Top250 = table.Column<float>(type: "REAL", nullable: false),
                    TotalPP = table.Column<float>(type: "REAL", nullable: false),
                    PPRatioFiltered = table.Column<float>(type: "REAL", nullable: false),
                    PPRatioUnfiltered = table.Column<float>(type: "REAL", nullable: false),
                    Percentile = table.Column<float>(type: "REAL", nullable: false),
                    Megametric = table.Column<float>(type: "REAL", nullable: false),
                    Megametric125 = table.Column<float>(type: "REAL", nullable: false),
                    Megametric75 = table.Column<float>(type: "REAL", nullable: false),
                    Megametric40 = table.Column<float>(type: "REAL", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Leaderboards", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Leaderboards_ModifiersRating_ModifiersRatingId",
                        column: x => x.ModifiersRatingId,
                        principalTable: "ModifiersRating",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "Scores",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Timepost = table.Column<int>(type: "INTEGER", nullable: false),
                    Pp = table.Column<float>(type: "REAL", nullable: false),
                    AccPP = table.Column<float>(type: "REAL", nullable: false),
                    TechPP = table.Column<float>(type: "REAL", nullable: false),
                    PassPP = table.Column<float>(type: "REAL", nullable: false),
                    BonusPp = table.Column<float>(type: "REAL", nullable: false),
                    Weight = table.Column<float>(type: "REAL", nullable: false),
                    PlayerId = table.Column<string>(type: "TEXT", maxLength: 25, nullable: false),
                    Rank = table.Column<int>(type: "INTEGER", nullable: false),
                    LeaderboardId = table.Column<string>(type: "TEXT", maxLength: 25, nullable: false),
                    Accuracy = table.Column<float>(type: "REAL", nullable: false),
                    Modifiers = table.Column<string>(type: "TEXT", nullable: false),
                    FC = table.Column<bool>(type: "INTEGER", nullable: false),
                    FCAcc = table.Column<float>(type: "REAL", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Scores", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Scores_Leaderboards_LeaderboardId",
                        column: x => x.LeaderboardId,
                        principalTable: "Leaderboards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Scores_Players_PlayerId",
                        column: x => x.PlayerId,
                        principalTable: "Players",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Leaderboards_ModifiersRatingId",
                table: "Leaderboards",
                column: "ModifiersRatingId");

            migrationBuilder.CreateIndex(
                name: "IX_Scores_LeaderboardId",
                table: "Scores",
                column: "LeaderboardId");

            migrationBuilder.CreateIndex(
                name: "IX_Scores_PlayerId",
                table: "Scores",
                column: "PlayerId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Scores");

            migrationBuilder.DropTable(
                name: "Leaderboards");

            migrationBuilder.DropTable(
                name: "Players");

            migrationBuilder.DropTable(
                name: "ModifiersRating");
        }
    }
}

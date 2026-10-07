using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using portaBLe.DB;
using portaBLe.Services;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace portaBLe.Pages
{
    public class DatabaseMetricsModel : BasePageModel
    {
        public List<DatabaseConfig> AllDatabases { get; set; }
        public string Database1 { get; set; }
        public string Database2 { get; set; }
        public string SelectedCharacteristic { get; set; } = "";
        public List<string> Characteristics { get; set; }
        
        public DatabaseMetrics Db1Metrics { get; set; }
        public DatabaseMetrics Db2Metrics { get; set; }

        public DatabaseMetricsModel(IDynamicDbContextService dbService) : base(dbService)
        {
        }

        public async Task<IActionResult> OnGetAsync(string db1 = null, string db2 = null, string characteristic = "")
        {
            AllDatabases = await _dbService.GetAvailableDatabasesAsync();
            
            // Default to first two databases if not specified
            if (string.IsNullOrEmpty(db1) && AllDatabases.Count > 0)
                db1 = AllDatabases[0].FileName;
            if (string.IsNullOrEmpty(db2) && AllDatabases.Count > 1)
                db2 = AllDatabases[1].FileName;
            
            Database1 = db1;
            Database2 = db2;
            SelectedCharacteristic = characteristic;

            // Get available characteristics from first database
            using (var context1 = (Services.DynamicDbContext)_dbService.CreateContext(Database1))
            {
                Characteristics = await context1.Leaderboards
                    .Select(l => l.ModeName)
                    .Distinct()
                    .OrderBy(m => m)
                    .ToListAsync();
            }

            // Load metrics from Stats table for both databases
            if (!string.IsNullOrEmpty(Database1))
            {
                using var context1 = (Services.DynamicDbContext)_dbService.CreateContext(Database1);
                Db1Metrics = await LoadMetricsFromStats(context1, SelectedCharacteristic);
            }

            if (!string.IsNullOrEmpty(Database2))
            {
                using var context2 = (Services.DynamicDbContext)_dbService.CreateContext(Database2);
                Db2Metrics = await LoadMetricsFromStats(context2, SelectedCharacteristic);
            }

            return Page();
        }

        private async Task<DatabaseMetrics> LoadMetricsFromStats(Services.DynamicDbContext context, string characteristic)
        {
            // Find the stats row for the specified characteristic (empty string for "All")
            var statsQuery = context.Stats.AsQueryable();
            
            DB.Stats stats;
            if (string.IsNullOrEmpty(characteristic))
            {
                stats = await statsQuery.FirstOrDefaultAsync(s => s.ModeName == "" || s.ModeName == null);
            }
            else
            {
                stats = await statsQuery.FirstOrDefaultAsync(s => s.ModeName == characteristic);
            }

            // If stats don't exist, return empty metrics
            if (stats == null)
            {
                return new DatabaseMetrics();
            }

            // Map Stats entity to DatabaseMetrics
            return new DatabaseMetrics
            {
                // Outliers
                TotalOutliers = stats.TotalOutlier,
                AverageOutlierPercentage = stats.AvgOutlierPercentage,

                // Megametrics
                AvgMegametric = stats.AvgMegametric,
                AvgMegametric125 = stats.AvgMegametric125,
                AvgMegametric75 = stats.AvgMegametric75,
                AvgMegametric40 = stats.AvgMegametric40,

                // Score counts
                ScoresAbove600 = stats.PpCount600,
                ScoresAbove700 = stats.PpCount700,
                ScoresAbove800 = stats.PpCount800,
                ScoresAbove900 = stats.PpCount900,
                ScoresAbove1000 = stats.PpCount1000,

                // Highest ratings
                HighestStars = stats.HighestStarRating,
                HighestAccRating = stats.HighestAccRating,
                HighestPassRating = stats.HighestPassRating,
                HighestTechRating = stats.HighestTechRating,

                // Highest PP scores
                HighestPP = stats.Top1PP,
                HighestAccPP = stats.Top1AccPP,
                HighestTechPP = stats.Top1TechPP,
                HighestPassPP = stats.Top1PassPP,

                // Total PP - using averages from Stats
                PpTop1 = stats.Top1PP,
                PpTop10 = stats.Top10PP,
                PpTop100 = stats.Top100PP,
                PpTop1000 = stats.Top1000PP,
                PpTop2000 = stats.Top2000PP,
                PpTop5000 = stats.Top5000PP,
                PpTop10000 = stats.Top10000PP,

                // Acc PP
                AccPpTop1 = stats.Top1AccPP,
                AccPpTop10 = stats.Top10AccPP,
                AccPpTop100 = stats.Top100AccPP,
                AccPpTop1000 = stats.Top1000AccPP,
                AccPpTop2000 = stats.Top2000AccPP,
                AccPpTop5000 = stats.Top5000AccPP,
                AccPpTop10000 = stats.Top10000AccPP,

                // Tech PP
                TechPpTop1 = stats.Top1TechPP,
                TechPpTop10 = stats.Top10TechPP,
                TechPpTop100 = stats.Top100TechPP,
                TechPpTop1000 = stats.Top1000TechPP,
                TechPpTop2000 = stats.Top2000TechPP,
                TechPpTop5000 = stats.Top5000TechPP,
                TechPpTop10000 = stats.Top10000TechPP,

                // Pass PP
                PassPpTop1 = stats.Top1PassPP,
                PassPpTop10 = stats.Top10PassPP,
                PassPpTop100 = stats.Top100PassPP,
                PassPpTop1000 = stats.Top1000PassPP,
                PassPpTop2000 = stats.Top2000PassPP,
                PassPpTop5000 = stats.Top5000PassPP,
                PassPpTop10000 = stats.Top10000PassPP
            };
        }

        public class DatabaseMetrics
        {
            // Outliers
            public int TotalOutliers { get; set; }
            public float AverageOutlierPercentage { get; set; }

            // Megametrics
            public float AvgMegametric { get; set; }
            public float AvgMegametric125 { get; set; }
            public float AvgMegametric75 { get; set; }
            public float AvgMegametric40 { get; set; }

            // Score counts
            public int ScoresAbove600 { get; set; }
            public int ScoresAbove700 { get; set; }
            public int ScoresAbove800 { get; set; }
            public int ScoresAbove900 { get; set; }
            public int ScoresAbove1000 { get; set; }

            // Highest ratings
            public float HighestStars { get; set; }
            public float HighestAccRating { get; set; }
            public float HighestPassRating { get; set; }
            public float HighestTechRating { get; set; }

            // Highest PP scores
            public float HighestPP { get; set; }
            public float HighestAccPP { get; set; }
            public float HighestTechPP { get; set; }
            public float HighestPassPP { get; set; }

            // Total PP for Rank
            public float PpTop1 { get; set; }
            public float PpTop10 { get; set; }
            public float PpTop100 { get; set; }
            public float PpTop1000 { get; set; }
            public float PpTop2000 { get; set; }
            public float PpTop5000 { get; set; }
            public float PpTop10000 { get; set; }

            // Acc PP for Rank
            public float AccPpTop1 { get; set; }
            public float AccPpTop10 { get; set; }
            public float AccPpTop100 { get; set; }
            public float AccPpTop1000 { get; set; }
            public float AccPpTop2000 { get; set; }
            public float AccPpTop5000 { get; set; }
            public float AccPpTop10000 { get; set; }

            // Tech PP for Rank
            public float TechPpTop1 { get; set; }
            public float TechPpTop10 { get; set; }
            public float TechPpTop100 { get; set; }
            public float TechPpTop1000 { get; set; }
            public float TechPpTop2000 { get; set; }
            public float TechPpTop5000 { get; set; }
            public float TechPpTop10000 { get; set; }

            // Pass PP for Rank
            public float PassPpTop1 { get; set; }
            public float PassPpTop10 { get; set; }
            public float PassPpTop100 { get; set; }
            public float PassPpTop1000 { get; set; }
            public float PassPpTop2000 { get; set; }
            public float PassPpTop5000 { get; set; }
            public float PassPpTop10000 { get; set; }
        }
    }
}

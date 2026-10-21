using Microsoft.AspNetCore.Mvc.RazorPages;
using portaBLe.Refresh;
using System.Text.Json;

namespace portaBLe.Pages
{
    // Shows wwwroot/balance-tuning.json, written by BalanceTuner.Run (called from Program.Main). Nothing is computed here.
    public class BalanceTuningModel : PageModel
    {
        private readonly IWebHostEnvironment _env;

        public BalanceTuningModel(IWebHostEnvironment env)
        {
            _env = env;
        }

        public BalanceTuningResult? Result { get; set; }
        public PpFormulaResult? Formula { get; set; }
        public string ChartDataJson { get; set; } = "{}";

        // Where each constant lives in the code
        public static readonly Dictionary<string, string> Locations = new()
        {
            [nameof(RatingConstants.PassCalibration)] = "PASS_CALIBRATION_FACTOR in Analyzer AnalyzeMap.cs",
            [nameof(RatingConstants.OneSaberNerf)] = "ONE_SABER_NERF in Analyzer AnalyzeMap.cs",
            [nameof(RatingConstants.TechScaler)] = "BALANCED_TECH_SCALER in Analyzer AnalyzeMap.cs (not tuned: keeps tech ratings on their usual scale)",
            [nameof(RatingConstants.TechWeight)] = "TechWeight in RatingAPI Pp/PpFormula.cs (tech pp per tech rating)",
            [nameof(RatingConstants.AccScale)] = "ACC_SCALE in RatingAPI AccRating.cs (reference, not tuned)",
            [nameof(RatingConstants.AccOffset)] = "ACC_OFFSET in RatingAPI AccRating.cs",
        };

        public static double Get(RatingConstants c, string name) => name switch
        {
            nameof(RatingConstants.PassCalibration) => c.PassCalibration,
            nameof(RatingConstants.OneSaberNerf) => c.OneSaberNerf,
            nameof(RatingConstants.TechScaler) => c.TechScaler,
            nameof(RatingConstants.TechWeight) => c.TechWeight,
            nameof(RatingConstants.AccScale) => c.AccScale,
            nameof(RatingConstants.AccOffset) => c.AccOffset,
            _ => 0,
        };

        // Median pp at the predicted acc in a star range, or null when the range has fewer than 10 maps
        public static double? BandPp(BalancePoint p, string band) => p.Bands.FirstOrDefault(b => b.Band == band && b.Maps >= 10)?.PpAtPredicted;

        public void OnGet()
        {
            Formula = BalanceTuner.LoadPpFormula(_env.WebRootPath);
            Result = BalanceTuner.LoadResult(_env.WebRootPath);
            if (Result == null) return;

            ChartDataJson = JsonSerializer.Serialize(new
            {
                sweeps = Result.Sweeps.Select(s => new
                {
                    knob = s.Knob,
                    target = s.Target,
                    round = s.Round,
                    suggested = s.Suggested,
                    values = s.Points.Select(p => p.Value),
                    targets = s.Points.Select(p => p.Target),
                    rankCorrelation = s.Points.Select(p => p.RankCorrelationTop1000),
                }),
            }, new JsonSerializerOptions { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals });
        }
    }
}

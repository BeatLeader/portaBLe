using Microsoft.AspNetCore.Mvc.RazorPages;
using portaBLe.Refresh;
using System.Text.Json;

namespace portaBLe.Pages
{
    // Shows wwwroot/curve-experiments.json, written by CurveExperiment.Run (called from Program.Main). Nothing is computed here.
    public class CurveExperimentsModel : PageModel
    {
        private readonly IWebHostEnvironment _env;

        public CurveExperimentsModel(IWebHostEnvironment env)
        {
            _env = env;
        }

        public CurveExperimentResult? Result { get; set; }
        public string ChartDataJson { get; set; } = "{}";

        public void OnGet()
        {
            Result = CurveExperiment.Load(_env.WebRootPath);
            if (Result == null) return;

            ChartDataJson = JsonSerializer.Serialize(new
            {
                names = Result.Candidates.Select(c => c.Settings.Name),
                samples = Result.CurveSamples.Select(s => new { predicted = s.PredictedAcc, curves = s.Curves }),
                bands = Result.StarBands,
                megametricCv = Result.Candidates.Select(c => c.ByStars.Select(b => b.MegametricMaps >= 10 ? (double?)b.MegametricCv : null)),
                maxPpMedian = Result.Candidates.Select(c => c.ByStars.Select(b => b.Maps > 0 ? (double?)b.MaxPpMedian : null)),
                topPlayersPp = Result.Candidates.Select(c => c.ByStars.Select(b => b.TopPlayersScores >= 30 ? (double?)b.TopPlayersMedianPp : null)),
                top50Share = Result.Candidates.Select(c => c.ByStars.Select(b => b.MegametricMaps >= 10 ? (double?)(b.Top50ShareMedian * 100) : null)),
            }, new JsonSerializerOptions { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals });
        }

        // Green when the candidate is better than the reference (first candidate), red when worse.
        public static string Compare(double value, double reference, bool higherIsBetter, double tolerance = 0.005)
        {
            double diff = value - reference;
            if (Math.Abs(diff) <= tolerance * Math.Max(1e-9, Math.Abs(reference))) return "";
            bool better = higherIsBetter ? diff > 0 : diff < 0;
            return better ? "text-success" : "text-danger";
        }
    }
}

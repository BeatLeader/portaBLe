using Microsoft.AspNetCore.Mvc.RazorPages;
using RC = global::RatingAPI.Controllers;
using System.Text.Json;

namespace portaBLe.Pages
{
    // Per-map acc curve for any predicted acc, computed with the same code CurvesRefresh uses (Curve.GetCurve with
    // RC.CurveSettings.Current). Nothing is read from or written to a database.
    // The map-dependent corrections (slider tilt, low note nerf, acc top compression, acc curve power, very low star power)
    // are applied by the page's script with the same formulas (Curve.GetCurve, CurveSettings.LowNoteMultiplier,
    // PpFormula.AccTop / AccCurve / CurvePowerAt). The curves sent have no note count, so no low note nerf.
    public class CurveExplorerModel : PageModel
    {
        public const double MinPredicted = 0.940;
        public const double MaxPredicted = 0.999;
        public const double Step = 0.0005;

        public string DataJson { get; set; } = "{}";
        public List<RC.CurveRow> Table { get; set; } = new();

        public void OnGet()
        {
            var generator = new RC.Curve();
            var lack = new RC.LackMapCalculation();
            var settings = RC.CurveSettings.Current;
            var formula = global::RatingAPI.Pp.PpFormula.Current;
            Table = settings.Table.OrderBy(r => r.PredictedAcc).ToList();

            var basePoints = generator.GetCurve(lack)
                .OrderBy(p => p.x)
                .Select(p => new[] { Math.Round(p.x * 100, 4), Math.Round(p.y, 5) })
                .ToList();

            int count = (int)Math.Round((MaxPredicted - MinPredicted) / Step) + 1;
            var curves = new List<object>(count);
            for (int i = 0; i < count; i++)
            {
                double predicted = Math.Round(MinPredicted + i * Step, 4);
                var row = settings.ParamsAt(predicted);
                var points = generator.GetCurve(lack, predicted)
                    .OrderBy(p => p.x)
                    .Select(p => new[] { Math.Round(p.x * 100, 4), Math.Round(p.y, 5) })
                    .ToList();
                curves.Add(new
                {
                    predicted = predicted * 100,
                    a = Math.Min(Math.Max(row.A, 0.951), 1.0) * 100,
                    tapered = row.A < 0.9995,
                    c = row.C,
                    k = row.K,
                    w = row.W,
                    k2 = row.K2,
                    p2 = row.P2,
                    isDefault = row.IsDefault,
                    points,
                });
            }

            DataJson = JsonSerializer.Serialize(new
            {
                enabled = settings.Enabled,
                basePoints,
                defaults = new { k = 4.86, w = 0.6, k2 = 2.88, p2 = 2.46 },
                // Corrections applied on top of the per-map curve (off by default on the page, the fields start at these)
                corrections = new
                {
                    sliderTilt = settings.SliderTilt,
                    sliderTiltRange = settings.SliderTiltRange * 100,
                    accTopKnee = formula.AccTopKnee * 100,
                    accTopSlope = formula.AccTopSlope,
                    accCurvePower = formula.AccCurvePower,
                    lowStarCurvePower = formula.LowStarCurvePower,
                    lowStarCurveStart = formula.LowStarCurveStart,
                    lowStarCurveEnd = formula.LowStarCurveEnd,
                    veryLowStarCurvePower = formula.VeryLowStarCurvePower,
                    veryLowStarCurveStart = formula.VeryLowStarCurveStart,
                    veryLowStarCurveEnd = formula.VeryLowStarCurveEnd,
                    lowNoteStart = settings.LowNoteStart,
                    lowNoteRefNotes = settings.LowNoteRefNotes,
                    lowNoteRefMultiplier = settings.LowNoteRefMultiplier,
                    lowNoteMinMultiplier = settings.LowNoteMinMultiplier,
                    shortStart = settings.ShortStart,
                    shortRefSpan = settings.ShortRefSpan,
                    shortRefMultiplier = settings.ShortRefMultiplier,
                    shortMinMultiplier = settings.ShortMinMultiplier,
                    starAccuracy = Math.Round(global::RatingAPI.Pp.PpCalculator.StarAccuracy * 100.0, 4),
                },
                curves,
            });
        }
    }
}

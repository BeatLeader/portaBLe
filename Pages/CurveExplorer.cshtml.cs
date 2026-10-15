using Microsoft.AspNetCore.Mvc.RazorPages;
using RC = global::RatingAPI.Controllers;
using System.Text.Json;

namespace portaBLe.Pages
{
    // Per-map acc curve for any predicted acc, as RatingAPI builds it now (Curve.GetCurve with RC.CurveSettings.Current).
    // Every curve on the slider's range is computed here with the same code CurvesRefresh uses, so the page shows exactly
    // the logic in use. Nothing is read from or written to a database.
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
            Table = settings.Table.OrderBy(r => r.PredictedAcc).ToList();

            var basePoints = generator.GetCurve(lack)
                .Where(p => p.x >= 0.6)
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
                    .Where(p => p.x >= 0.6)
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
                curves,
            });
        }
    }
}

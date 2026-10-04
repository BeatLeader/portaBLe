// Minimal copies of the RatingAPI DTOs that production Curve.cs / InferPublish.cs depend on.
// (RatingsController.cs itself is not compiled: it needs ASP.NET and, in the corpus branch,
// does not match the corpus analyzer's static Analyze API.)
using Analyzer.BeatmapScanner.Data;

namespace RatingAPI.Controllers
{
    public class LackMapCalculation
    {
        public double PassRating { get; set; } = 0;
        public double TechRating { get; set; } = 0;
        public double LowNoteNerf { get; set; } = 0;
        public double LinearPercentage { get; set; } = 0;
    }

    public class Point
    {
        public double x { get; set; } = 0;
        public double y { get; set; } = 0;

        public Point() { }
        public Point(double x, double y) { this.x = x; this.y = y; }

        public List<Point> ToPoints(List<(double x, double y)> curve)
        {
            var points = new List<Point>();
            foreach (var p in curve) points.Add(new(p.x, p.y));
            return points;
        }
    }
}

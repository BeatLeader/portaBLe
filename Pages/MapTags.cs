using portaBLe.DB;
using portaBLe.Refresh;

namespace portaBLe.Pages
{
    // A map's tags (LeaderboardsRefresh.DetermineTags) for the pages: its type from RatingAPI's tagging model (acc, tech,
    // midspeed, speed) and Linear, Fitbeat, Bomb Reset and Inline from the Analyzer's counts
    public static class MapTags
    {
        public record Tag(string Key, string Label, string Css, string Tooltip);

        public static readonly Tag[] All =
        {
            new("acc", "Acc", "map-tag-acc", "Map type (RatingAPI's tagging model on the acc, tech and pass ratings): accuracy"),
            new("tech", "Tech", "map-tag-tech", "Map type (RatingAPI's tagging model on the acc, tech and pass ratings): tech"),
            new("midspeed", "Midspeed", "map-tag-midspeed", "Map type (RatingAPI's tagging model on the acc, tech and pass ratings): midspeed"),
            new("speed", "Speed", "map-tag-speed", "Map type (RatingAPI's tagging model on the acc, tech and pass ratings): speed"),
            new("linear", "Linear", "map-tag-trait", $"At least {LeaderboardsRefresh.LinearPercentThreshold:P0} of the swings are linear"),
            new("fitbeat", "Fitbeat", "map-tag-trait", "Dense dodge and crouch walls for its length"),
            new("bombreset", "Bomb Reset", "map-tag-trait", $"At least {LeaderboardsRefresh.BombResetsPerMinuteThreshold:0.#} bomb resets per minute"),
            new("inline", "Inline", "map-tag-trait", $"At least {LeaderboardsRefresh.InlinesPerMinuteThreshold:0.#} inlines per minute (a note at most 1/4 beat behind another (either color) in the same column and row, hidden by it)"),
        };

        public static IEnumerable<Tag> Of(Leaderboard lb)
        {
            foreach (var tag in All)
            {
                bool has = tag.Key switch
                {
                    "linear" => lb.IsLinear,
                    "fitbeat" => lb.IsFitbeat,
                    "bombreset" => lb.IsBombReset,
                    "inline" => lb.IsInline,
                    _ => lb.MapType == tag.Key,
                };
                if (has) yield return tag;
            }
        }

        // The leaderboards with that tag (a Key of All; anything else: all of them)
        public static IQueryable<Leaderboard> Filter(IQueryable<Leaderboard> query, string? key) => key switch
        {
            "linear" => query.Where(l => l.IsLinear),
            "fitbeat" => query.Where(l => l.IsFitbeat),
            "bombreset" => query.Where(l => l.IsBombReset),
            "inline" => query.Where(l => l.IsInline),
            "acc" or "tech" or "midspeed" or "speed" => query.Where(l => l.MapType == key),
            _ => query,
        };

        // The badges of a map
        public static Microsoft.AspNetCore.Html.IHtmlContent Badges(Leaderboard lb)
        {
            var html = string.Join(" ", Of(lb).Select(t =>
                $"<span class=\"map-tag {t.Css}\" title=\"{System.Net.WebUtility.HtmlEncode(t.Tooltip)}\">{t.Label}</span>"));
            return new Microsoft.AspNetCore.Html.HtmlString(html);
        }
    }
}

using Microsoft.EntityFrameworkCore;
using portaBLe.Pages;

namespace portaBLe.Services
{
    // A player's style, from the maps of their front page (top FrontPage scores), each counted by its weighted pp: Acc at
    // AccMaxStars or under, Midspeed / Tech up to HighMaxStars and Speed / High Tech above (tech: the map's MapType), at the
    // score's stars (its speed modifier's). The style is the category with the most pp, and a second one too ("Tech / Midspeed")
    // when it has at least MixShare of it. Modifiers when at least ModifiersShare of it is on SS / FS / SF.
    public static class PlayerStyles
    {
        public const int FrontPage = 8;
        public const float AccMaxStars = 5f;
        public const float HighMaxStars = 9f;
        public const double MixShare = 0.3;
        public const double ModifiersShare = 0.5;

        public record Style(string Key, string Label, string Css, string Tooltip);

        public static readonly Style[] All =
        {
            new("acc", "Acc", "map-tag-acc", $"Front page pp mostly on maps of {AccMaxStars:0.#} stars or under"),
            new("midspeed", "Midspeed", "map-tag-midspeed", $"Front page pp mostly on non tech maps over {AccMaxStars:0.#} to {HighMaxStars:0.#} stars"),
            new("tech", "Tech", "map-tag-tech", $"Front page pp mostly on tech maps over {AccMaxStars:0.#} to {HighMaxStars:0.#} stars"),
            new("speed", "Speed", "map-tag-speed", $"Front page pp mostly on non tech maps over {HighMaxStars:0.#} stars"),
            new("hightech", "High Tech", "map-tag-hightech", $"Front page pp mostly on tech maps over {HighMaxStars:0.#} stars"),
            new("modifiers", "Modifiers", "map-tag-trait", $"At least {ModifiersShare:P0} of the front page pp with a speed modifier (SS, FS, SF)"),
        };

        public static Style? Find(string? key) => All.FirstOrDefault(s => s.Key == key);

        // The valid keys of a comma separated list (the Ranking page's style parameter), in All order: the included ones, and
        // the excluded ones written with a leading "-" (tech,-modifiers)
        public static (string[] Include, string[] Exclude) ParseKeys(string? keys)
        {
            var set = (keys ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();
            return (All.Where(s => set.Contains(s.Key)).Select(s => s.Key).ToArray(),
                All.Where(s => set.Contains("-" + s.Key)).Select(s => s.Key).ToArray());
        }

        public record PlayerStyle(string Primary, string? Secondary, bool Modifiers)
        {
            public bool Has(string key) => key == "modifiers" ? Modifiers : Primary == key || Secondary == key;

            // Any of the included styles (Acc to High Tech), Modifiers when included, and none of the excluded ones
            public bool Matches(string[] include, string[] exclude)
            {
                var styles = include.Where(k => k != "modifiers").ToArray();
                return (styles.Length == 0 || styles.Any(Has))
                    && (!include.Contains("modifiers") || Modifiers)
                    && !exclude.Any(Has);
            }

            public IEnumerable<Style> Styles()
            {
                yield return Find(Primary)!;
                if (Secondary != null) yield return Find(Secondary)!;
                if (Modifiers) yield return Find("modifiers")!;
            }
        }

        // The badges of a player's style
        public static Microsoft.AspNetCore.Html.IHtmlContent Badges(PlayerStyle? style)
        {
            if (style == null) return Microsoft.AspNetCore.Html.HtmlString.Empty;
            var html = string.Join(" ", style.Styles().Select(s =>
                $"<span class=\"map-tag {s.Css}\" title=\"{System.Net.WebUtility.HtmlEncode(s.Tooltip)}\">{s.Label}</span>"));
            return new Microsoft.AspNetCore.Html.HtmlString(html);
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<Task<Dictionary<string, PlayerStyle>>>> _byDatabase = new();

        // The database's player styles by player id, computed once (key: its file name)
        public static Task<Dictionary<string, PlayerStyle>> ForDatabaseAsync(string database, Func<DynamicDbContext> contextFactory) =>
            _byDatabase.GetOrAdd(database ?? "", _ => new Lazy<Task<Dictionary<string, PlayerStyle>>>(() => ComputeAsync(contextFactory))).Value;

        private static async Task<Dictionary<string, PlayerStyle>> ComputeAsync(Func<DynamicDbContext> contextFactory)
        {
            using var context = contextFactory();
            var maps = await context.Leaderboards.AsNoTracking()
                .Select(l => new
                {
                    l.Id, l.Stars, l.MapType,
                    SS = l.ModifiersRating != null ? l.ModifiersRating.SSStars : 0,
                    FS = l.ModifiersRating != null ? l.ModifiersRating.FSStars : 0,
                    SF = l.ModifiersRating != null ? l.ModifiersRating.SFStars : 0,
                })
                .ToDictionaryAsync(l => l.Id);

            // The front page's weights are 0.965^0 .. 0.965^(FrontPage - 1) (PlayersRefresh)
            float minWeight = MathF.Pow(0.965f, FrontPage - 1) - 0.0001f;
            var scores = await context.Scores.AsNoTracking()
                .Where(s => s.Weight >= minWeight && s.Pp > 0)
                .Select(s => new { s.PlayerId, s.LeaderboardId, s.Pp, s.Weight, s.Modifiers })
                .ToListAsync();

            var result = new Dictionary<string, PlayerStyle>();
            foreach (var player in scores.GroupBy(s => s.PlayerId))
            {
                var pp = new Dictionary<string, double>();
                double total = 0, modded = 0;
                foreach (var s in player.OrderByDescending(s => s.Pp).Take(FrontPage))
                {
                    if (!maps.TryGetValue(s.LeaderboardId, out var map)) continue;
                    string? speed = ScoreModifierFilter.SpeedModifier(s.Modifiers);
                    double stars = ScoreModifierFilter.Pick(speed, map.Stars, map.SS, map.FS, map.SF);
                    bool tech = map.MapType == "tech";
                    string key = stars <= AccMaxStars ? "acc"
                        : stars <= HighMaxStars ? (tech ? "tech" : "midspeed")
                        : (tech ? "hightech" : "speed");
                    double weighted = s.Pp * s.Weight;
                    pp[key] = pp.GetValueOrDefault(key) + weighted;
                    total += weighted;
                    if (speed != null) modded += weighted;
                }
                if (total <= 0) continue;

                var ranked = pp.OrderByDescending(p => p.Value).ToList();
                string? secondary = ranked.Count > 1 && ranked[1].Value >= MixShare * total ? ranked[1].Key : null;
                result[player.Key] = new PlayerStyle(ranked[0].Key, secondary, modded >= ModifiersShare * total);
            }
            return result;
        }
    }
}

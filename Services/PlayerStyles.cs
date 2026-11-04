using Microsoft.EntityFrameworkCore;
using portaBLe.DB;
using portaBLe.Pages;

namespace portaBLe.Services
{
    // A player's style, from the maps of their top TopShare of ranked plays (at least one), each counted by its weighted pp: Acc at AccMaxStars or under, Midspeed / Tech up to HighMaxStars and Speed /
    // High Tech above (tech: the map's MapType), at the score's stars (its speed modifier's). The style is the category with the most pp, and a second one too ("Tech / Midspeed")
    // when it has at least MixShare of it. Modifiers when at least ModifiersShare of it is on SS / FS / SF.
    public static class PlayerStyles
    {
        public const double TopShare = 0.25;
        public const float AccMaxStars = 5f;
        public const float HighMaxStars = 9f;
        public const double MixShare = 0.2;
        public const double ModifiersShare = 0.5;

        public record Style(string Key, string Label, string Css, string Tooltip);

        public static readonly Style[] All =
        {
            new("acc", "Acc", "map-tag-acc", $"Top {TopShare:P0} of plays' weighted pp mostly on maps of {AccMaxStars:0.#} stars or under"),
            new("midspeed", "Midspeed", "map-tag-midspeed", $"Top {TopShare:P0} of plays' weighted pp mostly on non tech maps over {AccMaxStars:0.#} to {HighMaxStars:0.#} stars"),
            new("tech", "Tech", "map-tag-tech", $"Top {TopShare:P0} of plays' weighted pp mostly on tech maps over {AccMaxStars:0.#} to {HighMaxStars:0.#} stars"),
            new("speed", "Speed", "map-tag-speed", $"Top {TopShare:P0} of plays' weighted pp mostly on non tech maps over {HighMaxStars:0.#} stars"),
            new("hightech", "High Tech", "map-tag-hightech", $"Top {TopShare:P0} of plays' weighted pp mostly on tech maps over {HighMaxStars:0.#} stars"),
            new("modifiers", "Modifiers", "map-tag-trait", $"At least {ModifiersShare:P0} of the top {TopShare:P0} of plays' weighted pp with a speed modifier (SS, FS, SF)"),
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

        // As stored in Player.Style: "primary[,secondary][,modifiers]", empty = none
        public static string Encode(PlayerStyle? style) =>
            style == null ? "" : string.Join(",", new[] { style.Primary, style.Secondary, style.Modifiers ? "modifiers" : null }.Where(k => k != null));

        public static PlayerStyle? Decode(string? stored)
        {
            var keys = (stored ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);
            var styles = keys.Where(k => k != "modifiers").ToArray();
            return styles.Length == 0 ? null : new PlayerStyle(styles[0], styles.ElementAtOrDefault(1), keys.Contains("modifiers"));
        }

        // Computes every player's style and stores it in Player.Style (RefreshEverything, after the scores' pp, weights and the stars)
        public static async Task StoreAsync(portaBLe.AppContext dbContext)
        {
            Console.WriteLine("Recalculating player styles");
            var styles = await ComputeAsync(dbContext);
            var players = (await dbContext.Players.AsNoTracking().Select(p => p.Id).ToListAsync())
                .Select(id => new Player { Id = id, Style = Encode(styles.GetValueOrDefault(id)) })
                .ToList();
            await dbContext.BulkUpdateAsync(players, options => options.ColumnInputExpression = c => new { c.Style });
            Console.WriteLine($"{styles.Count} player styles stored");
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<Task<Dictionary<string, PlayerStyle>>>> _byDatabase = new();

        // The database's player styles by player id, read once (key: its file name). Databases built before Player.Style
        // (none stored) compute them from their scores instead.
        public static Task<Dictionary<string, PlayerStyle>> ForDatabaseAsync(string database, Func<DynamicDbContext> contextFactory) =>
            _byDatabase.GetOrAdd(database ?? "", _ => new Lazy<Task<Dictionary<string, PlayerStyle>>>(() => LoadAsync(contextFactory))).Value;

        private static async Task<Dictionary<string, PlayerStyle>> LoadAsync(Func<DynamicDbContext> contextFactory)
        {
            using var context = contextFactory();
            var stored = await context.Players.AsNoTracking()
                .Where(p => p.Style != null && p.Style != "")
                .Select(p => new { p.Id, p.Style })
                .ToListAsync();
            if (stored.Count == 0) return await ComputeAsync(context);
            return stored.ToDictionary(p => p.Id, p => Decode(p.Style)!);
        }

        private static async Task<Dictionary<string, PlayerStyle>> ComputeAsync(DbContext context)
        {
            var maps = await context.Set<Leaderboard>().AsNoTracking()
                .Select(l => new
                {
                    l.Id, l.Stars, l.MapType,
                    SS = l.ModifiersRating != null ? l.ModifiersRating.SSStars : 0,
                    FS = l.ModifiersRating != null ? l.ModifiersRating.FSStars : 0,
                    SF = l.ModifiersRating != null ? l.ModifiersRating.SFStars : 0,
                })
                .ToDictionaryAsync(l => l.Id);

            var scores = await context.Set<Score>().AsNoTracking()
                .Where(s => s.Weight > 0 && s.Pp > 0)
                .Select(s => new { s.PlayerId, s.LeaderboardId, s.Pp, s.Weight, s.Modifiers })
                .ToListAsync();

            var result = new Dictionary<string, PlayerStyle>();
            foreach (var player in scores.GroupBy(s => s.PlayerId))
            {
                var pp = new Dictionary<string, double>();
                double total = 0, modded = 0;
                int count = Math.Max(1, (int)Math.Ceiling(TopShare * player.Count()));
                foreach (var s in player.OrderByDescending(s => s.Pp).Take(count))
                {
                    double weighted = s.Pp * s.Weight;
                    if (!maps.TryGetValue(s.LeaderboardId, out var map)) continue;
                    string? speed = ScoreModifierFilter.SpeedModifier(s.Modifiers);
                    double stars = ScoreModifierFilter.Pick(speed, map.Stars, map.SS, map.FS, map.SF);
                    bool tech = map.MapType == "tech";
                    string key = stars <= AccMaxStars ? "acc"
                        : stars <= HighMaxStars ? (tech ? "tech" : "midspeed")
                        : (tech ? "hightech" : "speed");
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

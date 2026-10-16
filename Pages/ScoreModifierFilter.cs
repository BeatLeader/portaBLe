using portaBLe.DB;

namespace portaBLe.Pages
{
    // Keeps only the scores whose speed modifier is one of the ticked ones, for the scores graph pages.
    // A score has at most one speed modifier. Score.Modifiers is a comma-separated list such as "SF,NF"; SFS is stored as "SF".
    public static class ScoreModifierFilter
    {
        public static IQueryable<Score> Apply(IQueryable<Score> query, bool none, bool ss, bool fs, bool sfs)
        {
            if (none && ss && fs && sfs) return query; // everything ticked: unfiltered

            // Wrapped in commas so a code only matches a whole entry of the list
            return query.Where(s =>
                (ss && ("," + (s.Modifiers ?? "").ToUpper() + ",").Contains(",SS,"))
                || (fs && ("," + (s.Modifiers ?? "").ToUpper() + ",").Contains(",FS,"))
                || (sfs && ("," + (s.Modifiers ?? "").ToUpper() + ",").Contains(",SF,"))
                || (none
                    && !("," + (s.Modifiers ?? "").ToUpper() + ",").Contains(",SS,")
                    && !("," + (s.Modifiers ?? "").ToUpper() + ",").Contains(",FS,")
                    && !("," + (s.Modifiers ?? "").ToUpper() + ",").Contains(",SF,")));
        }

        // The score's speed modifier as stored in ModifiersRating: "SS", "FS", "SF" or null
        public static string? SpeedModifier(string? modifiers)
        {
            if (string.IsNullOrEmpty(modifiers)) return null;
            foreach (var m in modifiers.ToUpper().Split(','))
            {
                var code = m.Trim();
                if (code == "SS" || code == "FS" || code == "SF") return code;
            }
            return null;
        }

        // The modifier's rating when the score has that modifier and it is rated, else the base rating
        public static double Pick(string? speed, double baseValue, double? ss, double? fs, double? sf)
        {
            double? value = speed switch { "SS" => ss, "FS" => fs, "SF" => sf, _ => null };
            return value is > 0 ? value.Value : baseValue;
        }
    }
}

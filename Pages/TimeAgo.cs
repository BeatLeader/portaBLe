namespace portaBLe.Pages
{
    // How long ago a score was set, from Score.Timepost (unix seconds, 0 when unknown):
    // days under a month, months under a year, years past that.
    public static class TimeAgo
    {
        public static string Format(int timepost)
        {
            if (timepost <= 0) return "";

            var set = DateTimeOffset.FromUnixTimeSeconds(timepost).UtcDateTime;
            var now = DateTime.UtcNow;

            // Whole calendar months elapsed
            int months = (now.Year - set.Year) * 12 + now.Month - set.Month;
            if (now.Day < set.Day || (now.Day == set.Day && now.TimeOfDay < set.TimeOfDay)) months--;

            if (months < 1) return Plural(Math.Max(0, (int)(now - set).TotalDays), "day");
            if (months < 12) return Plural(months, "month");
            return Plural(months / 12, "year");
        }

        // The exact date, for the tooltip
        public static string Exact(int timepost) =>
            timepost > 0 ? DateTimeOffset.FromUnixTimeSeconds(timepost).UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'") : "";

        static string Plural(int n, string unit) => $"{n} {unit}{(n == 1 ? "" : "s")} ago";
    }
}

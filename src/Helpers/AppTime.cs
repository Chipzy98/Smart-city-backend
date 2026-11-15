namespace SmartcityBackend.src.Helpers
{
    /// <summary>
    /// Sri Lanka local time helper.
    /// Azure Functions run in UTC, so every "hour of day" / "day of week"
    /// used for traffic logic must be converted to Asia/Colombo (UTC+05:30).
    /// </summary>
    public static class AppTime
    {
        private static readonly TimeZoneInfo SriLankaZone = ResolveZone();

        private static TimeZoneInfo ResolveZone()
        {
            // IANA id (Linux / Windows with ICU) → Windows id → fixed offset fallback
            foreach (var id in new[] { "Asia/Colombo", "Sri Lanka Standard Time" })
            {
                try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
                catch { /* try next */ }
            }
            return TimeZoneInfo.CreateCustomTimeZone(
                "SLST", TimeSpan.FromMinutes(330), "Sri Lanka Time", "Sri Lanka Time");
        }

        /// <summary>Current Sri Lanka local time.</summary>
        public static DateTime NowLocal() => ToLocal(DateTime.UtcNow);

        /// <summary>Convert a UTC DateTime to Sri Lanka local time.</summary>
        public static DateTime ToLocal(DateTime utc)
        {
            var asUtc = utc.Kind == DateTimeKind.Utc ? utc : DateTime.SpecifyKind(utc, DateTimeKind.Utc);
            return TimeZoneInfo.ConvertTimeFromUtc(asUtc, SriLankaZone);
        }

        /// <summary>Convert a Sri Lanka local DateTime to UTC.</summary>
        public static DateTime ToUtc(DateTime local)
        {
            var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
            return TimeZoneInfo.ConvertTimeToUtc(unspecified, SriLankaZone);
        }

        /// <summary>ISO-8601 string with the Sri Lanka offset, e.g. 2026-10-24T17:00:00+05:30.</summary>
        public static string ToIsoLocal(DateTime local) =>
            local.ToString("yyyy-MM-dd'T'HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) + "+05:30";

        public static bool IsRushHour(int hour) => hour is >= 7 and <= 9 or >= 17 and <= 19;

        public static bool IsWeekend(int dayOfWeek) => dayOfWeek is 0 or 6;
    }
}

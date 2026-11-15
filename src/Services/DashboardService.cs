using Microsoft.EntityFrameworkCore;
using SmartcityBackend.src.Data;
using SmartcityBackend.src.Helpers;
using SmartcityBackend.src.Services.Interfaces;

namespace SmartcityBackend.src.Services
{
    /// <summary>
    /// All three dashboards return the same field names (frontend reads one shape).
    /// "scope" tells the UI whether numbers are city-wide or personal.
    /// </summary>
    public class DashboardService : IDashboardService
    {
        private readonly SmartCityDbContext db;
        private readonly InsightService insights;

        public DashboardService(SmartCityDbContext db, InsightService insights)
        {
            this.db = db;
            this.insights = insights;
        }

        public async Task<object> GetAdminDashboardAsync()
        {
            return new
            {
                scope = "city",
                totalUsers = await db.SmartUser.CountAsync(x => x.IsActive),
                totalTrafficRecords = await db.SmartTrafficData.CountAsync(x => x.IsActive),
                totalWasteRecords = await db.SmartWasteRecord.CountAsync(x => x.IsActive),
                totalEnergyRecords = await db.SmartEnergyUsage.CountAsync(x => x.IsActive),
                totalCo2Saved = Math.Round((await db.SmartWasteRecord.Where(x => x.IsActive)
                    .SumAsync(x => (double?)x.Co2Saved)) ?? 0, 2),
                averageCongestion = await AverageCongestionLast24hAsync(),
                latestInsights = await insights.GetLatestAsync(5),
            };
        }

        public async Task<object> GetManagerDashboardAsync()
        {
            return new
            {
                scope = "city",
                totalUsers = await db.SmartUser.CountAsync(x => x.IsActive),
                totalTrafficRecords = await db.SmartTrafficData.CountAsync(x => x.IsActive),
                totalWasteRecords = await db.SmartWasteRecord.CountAsync(x => x.IsActive),
                totalEnergyRecords = await db.SmartEnergyUsage.CountAsync(x => x.IsActive),
                totalEnergyUsage = Math.Round((await db.SmartEnergyUsage.Where(x => x.IsActive)
                    .SumAsync(x => (double?)x.UsageUnits)) ?? 0, 1),
                averageCongestion = await AverageCongestionLast24hAsync(),
                latestInsights = await insights.GetLatestAsync(5),
            };
        }

        public async Task<object> GetUserDashboardAsync(Guid userId)
        {
            var latestUsage = await db.SmartEnergyUsage
                .Where(x => x.IsActive && x.UserId == userId)
                .OrderByDescending(x => x.RecordedAt)
                .Select(x => (float?)x.UsageUnits)
                .FirstOrDefaultAsync();

            return new
            {
                scope = "personal",
                totalWasteRecords = await db.SmartWasteRecord.CountAsync(x => x.IsActive && x.UserId == userId),
                totalCo2Saved = Math.Round((await db.SmartWasteRecord.Where(x => x.IsActive && x.UserId == userId)
                    .SumAsync(x => (double?)x.Co2Saved)) ?? 0, 2),
                totalEnergyRecords = await db.SmartEnergyUsage.CountAsync(x => x.IsActive && x.UserId == userId),
                latestMonthlyUsage = latestUsage,
                totalRoutes = await db.SmartRouteTraffic.CountAsync(x => x.IsActive && x.UserId == userId),
                averageCongestion = await AverageCongestionLast24hAsync(),
                latestInsights = await insights.GetLatestAsync(5),
            };
        }

        public async Task<object> GetTrendsAsync(Guid userId, string role, int days)
        {
            days = Math.Clamp(days, 3, 30);
            var personal = role == SmartCityPermissions.User;

            var todayLocal = AppTime.NowLocal().Date;
            var fromLocal = todayLocal.AddDays(-(days - 1));
            var fromUtc = AppTime.ToUtc(fromLocal);

            var traffic = await db.SmartTrafficData
                .Where(x => x.IsActive && x.RecordedAt >= fromUtc && x.Source != "csv")
                .Select(x => new { x.RecordedAt, x.CongestionLevel })
                .ToListAsync();

            var energy = await db.SmartEnergyUsage
                .Where(x => x.IsActive && x.RecordedAt >= fromUtc && (!personal || x.UserId == userId))
                .Select(x => new { x.RecordedAt, x.UsageUnits })
                .ToListAsync();

            var waste = await db.SmartWasteRecord
                .Where(x => x.IsActive && x.CreatedDateTime >= fromUtc && (!personal || x.UserId == userId))
                .Select(x => new { x.CreatedDateTime, x.Co2Saved })
                .ToListAsync();

            var result = Enumerable.Range(0, days).Select(i =>
            {
                var day = fromLocal.AddDays(i);
                var t = traffic.Where(x => AppTime.ToLocal(x.RecordedAt).Date == day).ToList();
                var e = energy.Where(x => AppTime.ToLocal(x.RecordedAt).Date == day).ToList();
                var w = waste.Where(x => AppTime.ToLocal(x.CreatedDateTime).Date == day).ToList();

                return new
                {
                    date = day.ToString("yyyy-MM-dd"),
                    label = day.ToString("ddd"),
                    avgCongestion = t.Count > 0 ? Math.Round(t.Average(x => x.CongestionLevel), 1) : (double?)null,
                    avgEnergyUsage = e.Count > 0 ? Math.Round(e.Average(x => x.UsageUnits), 1) : (double?)null,
                    wasteClassified = w.Count,
                    co2Saved = Math.Round(w.Sum(x => x.Co2Saved), 2),
                };
            }).ToList();

            return new { scope = personal ? "personal" : "city", days = result };
        }

        private async Task<double?> AverageCongestionLast24hAsync()
        {
            var since = DateTime.UtcNow.AddHours(-24);
            var avg = await db.SmartTrafficData
                .Where(x => x.IsActive && x.RecordedAt >= since && x.Source != "csv")
                .AverageAsync(x => (double?)x.CongestionLevel);
            return avg == null ? null : Math.Round(avg.Value, 1);
        }
    }
}

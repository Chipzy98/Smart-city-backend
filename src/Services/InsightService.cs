using Microsoft.EntityFrameworkCore;
using SmartcityBackend.src.Data;
using SmartcityBackend.src.Entities;

namespace SmartcityBackend.src.Services
{
    /// <summary>
    /// Writes AI / climate / traffic insights to smart_AIInsight so the dashboards
    /// show real, recent insights. Duplicate messages within a window are skipped.
    /// </summary>
    public class InsightService
    {
        private readonly SmartCityDbContext _db;

        public InsightService(SmartCityDbContext db)
        {
            _db = db;
        }

        public async Task AddAsync(string type, string message, TimeSpan? dedupeWindow = null, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(message)) return;
            if (message.Length > 1000) message = message[..1000];

            var since = DateTime.UtcNow - (dedupeWindow ?? TimeSpan.FromHours(6));
            var exists = await _db.SmartAIInsight.AnyAsync(
                x => x.IsActive && x.Type == type && x.Message == message && x.CreatedDateTime >= since, ct);
            if (exists) return;

            _db.SmartAIInsight.Add(new SmartAIInsight
            {
                AIInsightId = Guid.NewGuid(),
                Type = type,
                Message = message,
                CreatedDateTime = DateTime.UtcNow,
                IsActive = true,
            });
            await _db.SaveChangesAsync(ct);
        }

        /// <summary>Latest insights; times are marked as UTC so the browser shows local time correctly.</summary>
        public async Task<List<object>> GetLatestAsync(int take = 5, CancellationToken ct = default)
        {
            var rows = await _db.SmartAIInsight
                .Where(x => x.IsActive)
                .OrderByDescending(x => x.CreatedDateTime)
                .Take(take)
                .ToListAsync(ct);

            return rows.Select(x => (object)new
            {
                aiInsightId = x.AIInsightId,
                type = x.Type,
                message = x.Message,
                createdDateTime = DateTime.SpecifyKind(x.CreatedDateTime, DateTimeKind.Utc),
            }).ToList();
        }
    }
}

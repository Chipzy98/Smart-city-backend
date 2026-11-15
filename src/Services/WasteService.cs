using Microsoft.EntityFrameworkCore;
using SmartcityBackend.src.AI.Interfaces;
using SmartcityBackend.src.AI.Models;
using SmartcityBackend.src.Data;
using SmartcityBackend.src.Entities;
using SmartcityBackend.src.Services.Interfaces;
using SmartcityBackend.src.ViewModels;

namespace SmartcityBackend.src.Services
{
    public class WasteService : IWasteService
    {
        // ~5 MB image → ~6.7 M base64 chars. Claude API allows up to 10 MB base64 per image.
        private const int MaxBase64Length = 8_000_000;

        private readonly SmartCityDbContext db;
        private readonly IWasteAIService wasteAIService;

        public WasteService(
            SmartCityDbContext db,
            IWasteAIService wasteAIService)
        {
            this.db = db;
            this.wasteAIService = wasteAIService;
        }

        public async Task<object> GetMyWasteRecordsAsync(Guid userId, string q, int page, int pageSize)
        {
            var mine = db.SmartWasteRecord.Where(x => x.IsActive && x.UserId == userId);

            var query = mine;
            if (!string.IsNullOrWhiteSpace(q))
                query = query.Where(x => x.Category.Contains(q));

            var totalCount = await query.CountAsync();

            var data = await query
                .OrderByDescending(x => x.CreatedDateTime)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(x => new
                {
                    x.WasteRecordId,
                    x.ImageUrl,
                    x.Category,
                    x.Confidence,
                    x.Co2Saved,
                    x.CreatedDateTime
                })
                .ToListAsync();

            // Stats over ALL of the user's records (not only the current page)
            var categoryCounts = await mine
                .GroupBy(x => x.Category)
                .Select(g => new { category = g.Key, count = g.Count() })
                .ToListAsync();

            var stats = new
            {
                totalCount = categoryCounts.Sum(c => c.count),
                totalCo2Saved = Math.Round((await mine.SumAsync(x => (double?)x.Co2Saved)) ?? 0, 2),
                categoryCounts = categoryCounts.ToDictionary(c => c.category, c => c.count),
            };

            return new { totalCount, page, pageSize, data, stats };
        }

        public async Task<object> ClassifyWasteAsync(Guid userId, WasteClassifyRequestVM request)
        {
            var base64 = request.ImageBase64;
            if (!string.IsNullOrWhiteSpace(base64))
            {
                // Accept a full data URL too: "data:image/png;base64,AAAA..."
                var comma = base64.IndexOf(',');
                var semicolon = base64.IndexOf(';');
                if (base64.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma > 0)
                {
                    if (semicolon > 5 && semicolon < comma) request.MediaType ??= base64[5..semicolon];
                    base64 = base64[(comma + 1)..];
                }
                if (base64.Length > MaxBase64Length)
                    throw new ArgumentException("Image is too large. Please upload an image under 5 MB.");
            }

            // ✅ async — no sync-over-async blocking
            var aiResult = await wasteAIService.ClassifyAsync(new WasteAIInput
            {
                ImageUrl = request.ImageUrl,
                ImageBase64 = base64,
                MediaType = request.MediaType,
            });

            // Base64 is never stored in the DB — only the URL or "upload:<file name>"
            var storedImage = string.IsNullOrWhiteSpace(base64)
                ? request.ImageUrl
                : $"upload:{request.ImageUrl}";

            var record = new SmartWasteRecord
            {
                WasteRecordId = Guid.NewGuid(),
                UserId = userId,
                ImageUrl = storedImage.Length > 500 ? storedImage[..500] : storedImage,
                Category = aiResult.Category,
                Confidence = aiResult.Confidence,
                Co2Saved = aiResult.Co2Saved,
                IsActive = true,
                CreatedDateTime = DateTime.UtcNow
            };

            db.SmartWasteRecord.Add(record);
            await db.SaveChangesAsync();

            return new
            {
                record.WasteRecordId,
                category = aiResult.Category,
                confidence = aiResult.Confidence,
                co2Saved = aiResult.Co2Saved,
                message = "Waste classified successfully"
            };
        }
    }
}

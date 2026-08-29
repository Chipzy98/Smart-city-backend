using Microsoft.EntityFrameworkCore;
using SmartcityBackend.src.AI.Interfaces;
using SmartcityBackend.src.AI.Models;
using SmartcityBackend.src.Data;
using SmartcityBackend.src.Entities;
using SmartcityBackend.src.Services.Interfaces;
using SmartcityBackend.src.ViewModels;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SmartcityBackend.src.Services
{
    public class WasteService : IWasteService
    {
        private readonly SmartCityDbContext db;
        private IWasteAIService wasteAIService;

        public WasteService(
            SmartCityDbContext db,
            IWasteAIService wasteAIService)
        {
            this.db = db;
            this.wasteAIService = wasteAIService;
        }

        public async Task<object> GetMyWasteRecordsAsync(Guid userId, string q, int page, int pageSize)
        {
            var query = db.SmartWasteRecord
                .Where(x => x.IsActive && x.UserId == userId)
                .AsQueryable();

            if (!string.IsNullOrWhiteSpace(q))
            {
                query = query.Where(x => x.Category.Contains(q));
            }

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

            return new
            {
                totalCount,
                page,
                pageSize,
                data
            };
        }

        public async Task<object> ClassifyWasteAsync(Guid userId, WasteClassifyRequestVM request)
        {
            var aiResult = wasteAIService.Classify(new WasteAIInput
            {
                ImageUrl = request.ImageUrl
            });

            var record = new SmartWasteRecord
            {
                WasteRecordId = Guid.NewGuid(),
                UserId = userId,
                ImageUrl = request.ImageUrl,
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

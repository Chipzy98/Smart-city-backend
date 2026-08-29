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
    public class EnergyService : IEnergyService
    {
        private readonly SmartCityDbContext db;
        private readonly IEnergyAIService energyAIService;

        public EnergyService(
            SmartCityDbContext db,
            IEnergyAIService energyAIService)
        {
            this.db = db;
            this.energyAIService = energyAIService;
        }

        public async Task<object> GetMyEnergyUsageAsync(Guid userId, string q, int page, int pageSize)
        {
            var query = db.SmartEnergyUsage
                .Where(x => x.IsActive && x.UserId == userId)
                .AsQueryable();

            var totalCount = await query.CountAsync();

            var data = await query
                .OrderByDescending(x => x.RecordedAt)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(x => new
                {
                    x.EnergyUsageId,
                    x.UsageUnits,
                    x.Temperature,
                    x.Occupants,
                    x.RecordedAt
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

        public async Task<object> PredictEnergyAsync(Guid userId, EnergyPredictionRequestVM request)
        {
            var aiResult = energyAIService.Predict(new EnergyAIInput
            {
                UsageUnits = request.UsageUnits,
                Temperature = request.Temperature,
                Occupants = request.Occupants
            });

            var usage = new SmartEnergyUsage
            {
                EnergyUsageId = Guid.NewGuid(),
                UserId = userId,
                UsageUnits = request.UsageUnits,
                Temperature = request.Temperature,
                Occupants = request.Occupants,
                RecordedAt = DateTime.UtcNow,
                CreatedDateTime = DateTime.UtcNow,
                IsActive = true
            };

            db.SmartEnergyUsage.Add(usage);
            await db.SaveChangesAsync();

            return new
            {
                predictedBill = Math.Round(aiResult.PredictedBill, 2),
                currency = aiResult.Currency,
                tip = aiResult.Tip
            };
        }
    }
}

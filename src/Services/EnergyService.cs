using Microsoft.EntityFrameworkCore;
using SmartcityBackend.src.AI.Interfaces;
using SmartcityBackend.src.AI.Models;
using SmartcityBackend.src.Data;
using SmartcityBackend.src.Entities;
using SmartcityBackend.src.Helpers;
using SmartcityBackend.src.Services.Interfaces;
using SmartcityBackend.src.ViewModels;

namespace SmartcityBackend.src.Services
{
    public class EnergyService : IEnergyService
    {
        private readonly SmartCityDbContext _db;
        private readonly IEnergyAIService _energyAIService;

        public EnergyService(SmartCityDbContext db, IEnergyAIService energyAIService)
        {
            _db = db;
            _energyAIService = energyAIService;
        }

        // ── Get usage history ─────────────────────────────────────────────────
        public async Task<object> GetMyEnergyUsageAsync(Guid userId, string q, int page, int pageSize)
        {
            var query = _db.SmartEnergyUsage
                .Where(x => x.IsActive && x.UserId == userId)
                .AsQueryable();

            var totalCount = await query.CountAsync();

            // Stats over ALL of the user's records (not only the current page)
            var lastTwo = await query
                .OrderByDescending(x => x.RecordedAt)
                .Select(x => x.UsageUnits)
                .Take(2)
                .ToListAsync();

            var stats = new
            {
                totalCount,
                averageUsage = totalCount > 0
                    ? Math.Round(await query.AverageAsync(x => (double)x.UsageUnits), 1)
                    : 0,
                lastUsage = lastTwo.Count > 0 ? lastTwo[0] : (float?)null,
                previousUsage = lastTwo.Count > 1 ? lastTwo[1] : (float?)null,
            };

            var rows = await query
                .OrderByDescending(x => x.RecordedAt)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(x => new { x.EnergyUsageId, x.UsageUnits, x.Temperature, x.Occupants, x.RecordedAt })
                .ToListAsync();

            // Bill recalculated with the CEB tariff so older records (saved in USD) also show in LKR.
            var data = rows.Select(x => new
            {
                x.EnergyUsageId,
                x.UsageUnits,
                x.Temperature,
                x.Occupants,
                predictedBill = CebTariff.Calculate(CebTariff.AdjustForTemperature(x.UsageUnits, x.Temperature)).Total,
                currency = CebTariff.Currency,
                x.RecordedAt
            }).ToList();

            return new { totalCount, page, pageSize, data, stats };
        }

        // ── Predict + Save ────────────────────────────────────────────────────
        public async Task<object> PredictEnergyAsync(Guid userId, EnergyPredictionRequestVM request)
        {
            // ✅ Use async version — avoids sync-over-async deadlock
            var aiResult = await _energyAIService.PredictAsync(new EnergyAIInput
            {
                UsageUnits = request.UsageUnits,
                Temperature = request.Temperature,
                Occupants = request.Occupants,
            });

            var bill = (float)Math.Round(aiResult.PredictedBill, 2);

            // Save with predicted bill
            var usage = new SmartEnergyUsage
            {
                EnergyUsageId = Guid.NewGuid(),
                UserId = userId,
                UsageUnits = request.UsageUnits,
                Temperature = request.Temperature,
                Occupants = request.Occupants,
                PredictedBill = bill,
                RecordedAt = DateTime.UtcNow,
                CreatedDateTime = DateTime.UtcNow,
                IsActive = true,
            };

            _db.SmartEnergyUsage.Add(usage);
            await _db.SaveChangesAsync();

            return new
            {
                predictedBill = bill,
                currency = aiResult.Currency,
                tip = aiResult.Tip,
                billedUnits = aiResult.BilledUnits,
                energyCharge = aiResult.EnergyCharge,
                fixedCharge = aiResult.FixedCharge,
                sscl = aiResult.Sscl,
                perPerson = Math.Round(bill / Math.Max(1, request.Occupants), 2),
                tariff = aiResult.Tariff,
            };
        }
    }
}
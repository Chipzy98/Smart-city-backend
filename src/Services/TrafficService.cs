using Microsoft.EntityFrameworkCore;
using SmartcityBackend.src.AI.Interfaces;
using SmartcityBackend.src.AI.Models;
using SmartcityBackend.src.Data;
using SmartcityBackend.src.Services.Interfaces;
using SmartcityBackend.src.ViewModels;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SmartcityBackend.src.Services
{

    public class TrafficService : ITrafficService
    {
        private readonly SmartCityDbContext db;
        private ITrafficAIService trafficAIService;

        public TrafficService(
            SmartCityDbContext db,
            ITrafficAIService trafficAIService)
        {
            this.db = db;
            this.trafficAIService = trafficAIService;
        }

        public async Task<object> GetTrafficSummary(Guid userId, int status, string q, int page, int pageSize)
        {
            var query = db.SmartTrafficData
                .Where(x => x.IsActive)
                .AsQueryable();

            if (!string.IsNullOrWhiteSpace(q))
            {
                query = query.Where(x =>
                    x.Latitude.ToString().Contains(q) ||
                    x.Longitude.ToString().Contains(q));
            }

            var totalCount = await query.CountAsync();

            var data = await query
                .OrderByDescending(x => x.RecordedAt)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(x => new
                {
                    x.TrafficDataId,
                    x.Latitude,
                    x.Longitude,
                    x.CongestionLevel,
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

        public Task<object> PredictTrafficAsync(TrafficPredictionRequestVM request)
        {
            var aiResult = trafficAIService.Predict(new TrafficAIInput
            {
                Hour = request.Hour,
                Day = request.Day,
                Weather = request.Weather,
                VehicleCount = request.VehicleCount
            });

            object result = new
            {
                predictedCongestionLevel = Math.Round(aiResult.PredictedCongestionLevel, 2),
                status = aiResult.Status,
                suggestion = aiResult.Suggestion
            };

            return Task.FromResult(result);
        }
    }
}

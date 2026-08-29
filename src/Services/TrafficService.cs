using Microsoft.EntityFrameworkCore;
using SmartcityBackend.src.AI.Interfaces;
using SmartcityBackend.src.AI.Models;
using SmartcityBackend.src.Data;
using SmartcityBackend.src.Entities;
using SmartcityBackend.src.Services.Interfaces;
using SmartcityBackend.src.ViewModels;

namespace SmartcityBackend.src.Services
{
    public class TrafficService : ITrafficService
    {
        private readonly SmartCityDbContext _db;
        private readonly ITrafficAIService _trafficAI;
        private readonly IRouteTrafficAIService _routeAI;

        public TrafficService(
            SmartCityDbContext db,
            ITrafficAIService trafficAI,
            IRouteTrafficAIService routeAI)
        {
            _db = db;
            _trafficAI = trafficAI;
            _routeAI = routeAI;
        }

        // ── Live traffic summary ──────────────────────────────────────────────
        public async Task<object> GetTrafficSummary(
            Guid userId, int status, string q, int page, int pageSize)
        {
            var query = _db.SmartTrafficData.Where(x => x.IsActive).AsQueryable();

            if (!string.IsNullOrWhiteSpace(q))
                query = query.Where(x =>
                    x.Latitude.ToString().Contains(q) ||
                    x.Longitude.ToString().Contains(q));

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

            return new { totalCount, page, pageSize, data };
        }

        // ── Single-point prediction ───────────────────────────────────────────
        public async Task<object> PredictTrafficAsync(TrafficPredictionRequestVM request)
        {
            var aiResult = await _trafficAI.PredictAsync(new TrafficAIInput
            {
                Hour = request.Hour,
                Day = request.Day,
                Weather = request.Weather,
                VehicleCount = request.VehicleCount,
            });

            return new
            {
                predictedCongestionLevel = Math.Round(aiResult.PredictedCongestionLevel, 2),
                status = aiResult.Status,
                suggestion = aiResult.Suggestion,
            };
        }

        // ── Route A → B analysis ──────────────────────────────────────────────
        public async Task<object> AnalyseRouteAsync(Guid userId, RouteTrafficRequestVM request)
        {
            var now = DateTime.UtcNow;

            var aiResult = await _routeAI.AnalyseRouteAsync(new RouteTrafficAIInput
            {
                OriginName = request.OriginName,
                OriginLat = request.OriginLat,
                OriginLng = request.OriginLng,
                DestinationName = request.DestinationName,
                DestinationLat = request.DestinationLat,
                DestinationLng = request.DestinationLng,
                Hour = now.Hour,
                DayOfWeek = (int)now.DayOfWeek,
                Weather = request.Weather,
            });

            // Persist to DB
            var record = new SmartRouteTraffic
            {
                RouteTrafficId = Guid.NewGuid(),
                UserId = userId,
                OriginName = request.OriginName,
                OriginLat = (decimal)request.OriginLat,
                OriginLng = (decimal)request.OriginLng,
                DestinationName = request.DestinationName,
                DestinationLat = (decimal)request.DestinationLat,
                DestinationLng = (decimal)request.DestinationLng,
                DistanceKm = aiResult.DistanceKm,
                EstimatedMinutes = aiResult.EstimatedMinutes,
                CongestionLevel = aiResult.CongestionLevel,
                CongestionStatus = aiResult.CongestionStatus,
                Suggestion = aiResult.Suggestion,
                RiskScore = aiResult.RiskScore,
                Hour = now.Hour,
                DayOfWeek = (int)now.DayOfWeek,
                Weather = request.Weather,
                IsActive = true,
                CreatedDateTime = now,
            };

            _db.SmartRouteTraffic.Add(record);
            await _db.SaveChangesAsync();

            return new
            {
                routeTrafficId = record.RouteTrafficId,
                originName = record.OriginName,
                destinationName = record.DestinationName,
                distanceKm = Math.Round(aiResult.DistanceKm, 1),
                estimatedMinutes = aiResult.EstimatedMinutes,
                congestionLevel = Math.Round(aiResult.CongestionLevel, 1),
                congestionStatus = aiResult.CongestionStatus,
                suggestion = aiResult.Suggestion,
                riskScore = Math.Round(aiResult.RiskScore, 1),
            };
        }

        // ── Route history ─────────────────────────────────────────────────────
        public async Task<object> GetMyRouteHistoryAsync(Guid userId, int page, int pageSize)
        {
            var query = _db.SmartRouteTraffic
                .Where(x => x.IsActive && x.UserId == userId)
                .OrderByDescending(x => x.CreatedDateTime);

            var totalCount = await query.CountAsync();
            var data = await query
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(x => new
                {
                    x.RouteTrafficId,
                    x.OriginName,
                    x.DestinationName,
                    x.DistanceKm,
                    x.EstimatedMinutes,
                    x.CongestionLevel,
                    x.CongestionStatus,
                    x.RiskScore,
                    x.CreatedDateTime,
                })
                .ToListAsync();

            return new { totalCount, page, pageSize, data };
        }
    }
}
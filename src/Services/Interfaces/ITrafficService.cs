using SmartcityBackend.src.ViewModels;

namespace SmartcityBackend.src.Services.Interfaces
{
    public interface ITrafficService
    {
        Task<object> GetTrafficSummary(Guid userId, int status, string q, int page, int pageSize);
        Task<object> PredictTrafficAsync(TrafficPredictionRequestVM request);
        Task<object> AnalyseRouteAsync(Guid userId, RouteTrafficRequestVM request);
        Task<object> GetMyRouteHistoryAsync(Guid userId, int page, int pageSize);
        /// <summary>Best A → B route with live Google traffic (null when Google is unavailable).</summary>
        Task<object?> GetLiveRouteAsync(double oLat, double oLng, double dLat, double dLng);
    }
}
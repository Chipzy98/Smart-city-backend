using SmartcityBackend.src.AI.Models;

namespace SmartcityBackend.src.AI.Interfaces
{
    public interface IRouteTrafficAIService
    {
        Task<RouteTrafficAIOutput> AnalyseRouteAsync(RouteTrafficAIInput input, CancellationToken ct = default);
    }
}
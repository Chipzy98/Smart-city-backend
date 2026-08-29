using SmartcityBackend.src.AI.Models;

namespace SmartcityBackend.src.AI.Interfaces;

public interface ITrafficAIService
{
    /// <summary>Synchronous wrapper — prefer <see cref="PredictAsync"/> in new code.</summary>
    TrafficAIOutput Predict(TrafficAIInput input);

    /// <summary>Async congestion prediction with cancellation support.</summary>
    Task<TrafficAIOutput> PredictAsync(TrafficAIInput input, CancellationToken ct = default);
}
using SmartcityBackend.src.AI.Models;

namespace SmartcityBackend.src.AI.Interfaces;

public interface IEnergyAIService
{
    /// <summary>Synchronous wrapper — prefer <see cref="PredictAsync"/> in new code.</summary>
    EnergyAIOutput Predict(EnergyAIInput input);

    /// <summary>Async bill prediction with cancellation support.</summary>
    Task<EnergyAIOutput> PredictAsync(EnergyAIInput input, CancellationToken ct = default);
}
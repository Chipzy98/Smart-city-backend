using SmartcityBackend.src.AI.Models;

namespace SmartcityBackend.src.AI.Interfaces;

public interface IWasteAIService
{
    /// <summary>Synchronous wrapper — prefer <see cref="ClassifyAsync"/> in new code.</summary>
    WasteAIOutput Classify(WasteAIInput input);

    /// <summary>Async waste classification with cancellation support.</summary>
    Task<WasteAIOutput> ClassifyAsync(WasteAIInput input, CancellationToken ct = default);
}
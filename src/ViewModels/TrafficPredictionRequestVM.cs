namespace SmartcityBackend.src.ViewModels
{
    /// <summary>"What-if" scenario prediction (manual inputs).</summary>
    public class TrafficPredictionRequestVM
    {
        public float Hour { get; set; }
        public float Day { get; set; }
        public float Weather { get; set; }

        /// <summary>Optional — only used by the Claude fallback (kept for compatibility).</summary>
        public float VehicleCount { get; set; }

        public float? Temperature { get; set; }
        public double? Latitude { get; set; }
        public double? Longitude { get; set; }
        public string? LocationName { get; set; }
    }
}

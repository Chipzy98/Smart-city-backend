namespace SmartcityBackend.src.AI.Models
{
    public class RouteTrafficAIInput
    {
        public string OriginName { get; set; } = string.Empty;
        public double OriginLat { get; set; }
        public double OriginLng { get; set; }
        public string DestinationName { get; set; } = string.Empty;
        public double DestinationLat { get; set; }
        public double DestinationLng { get; set; }
        public int Hour { get; set; }
        public int DayOfWeek { get; set; }
        public int Weather { get; set; }
    }

    public class RouteTrafficAIOutput
    {
        public float DistanceKm { get; set; }
        public int EstimatedMinutes { get; set; }
        public float CongestionLevel { get; set; }
        public string CongestionStatus { get; set; } = string.Empty;
        public string Suggestion { get; set; } = string.Empty;
        public float RiskScore { get; set; }
    }
}
namespace SmartcityBackend.src.AI.Models
{
    /// <summary>One alternative route option returned by Claude.</summary>
    public class RouteOption
    {
        public string RouteName { get; set; } = string.Empty;  // eg: "Via E01 Southern Expressway"
        public string RoadNames { get; set; } = string.Empty;  // eg: "E01 → A1 → Kandy Road"
        public float DistanceKm { get; set; }
        public int EstimatedMinutes { get; set; }
        public float CongestionLevel { get; set; }   // 0–100
        public string CongestionStatus { get; set; } = string.Empty;
        public float RiskScore { get; set; }   // 0–100
        public string Reason { get; set; } = string.Empty;  // why recommend this route
        public bool IsRecommended { get; set; }   // Claude's top pick
        public List<RouteWaypoint> Waypoints { get; set; } = new(); // lat/lng path
    }

    /// <summary>Approximate waypoint for drawing route on map.</summary>
    public class RouteWaypoint
    {
        public double Lat { get; set; }
        public double Lng { get; set; }
    }

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
        public List<RouteOption> Routes { get; set; } = new();
        public string Summary { get; set; } = string.Empty;
        public float DistanceKm { get; set; }  // best route
        public int EstimatedMinutes { get; set; }  // best route
        public float CongestionLevel { get; set; }  // best route
        public string CongestionStatus { get; set; } = string.Empty;
        public string Suggestion { get; set; } = string.Empty;
        public float RiskScore { get; set; }
    }
}
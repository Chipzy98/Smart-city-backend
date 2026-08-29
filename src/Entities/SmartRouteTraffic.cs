using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SmartcityBackend.src.Entities
{
    [Table("smart_RouteTraffic")]
    public class SmartRouteTraffic
    {
        [Key]
        public Guid RouteTrafficId { get; set; }

        public Guid UserId { get; set; }

        // Origin
        public string OriginName { get; set; } = string.Empty;
        public decimal OriginLat { get; set; }
        public decimal OriginLng { get; set; }

        // Destination
        public string DestinationName { get; set; } = string.Empty;
        public decimal DestinationLat { get; set; }
        public decimal DestinationLng { get; set; }

        // AI result
        public float DistanceKm { get; set; }
        public int EstimatedMinutes { get; set; }
        public float CongestionLevel { get; set; }
        public string CongestionStatus { get; set; } = string.Empty;
        public string Suggestion { get; set; } = string.Empty;
        public float RiskScore { get; set; }

        // Conditions at time of query
        public int Hour { get; set; }
        public int DayOfWeek { get; set; }
        public int Weather { get; set; }

        public bool IsActive { get; set; } = true;
        public DateTime CreatedDateTime { get; set; }
    }
}
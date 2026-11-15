using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SmartcityBackend.src.Entities
{
    /// <summary>
    /// One observed traffic sample (TomTom live flow, CSV import, or legacy row).
    /// This table is also the training dataset for the traffic ML model.
    /// New columns are nullable so older rows keep working.
    /// </summary>
    [Table("smart_TrafficData")]
    public class SmartTrafficData
    {
        [Key]
        public Guid TrafficDataId { get; set; }

        public decimal Latitude { get; set; }

        public decimal Longitude { get; set; }

        /// <summary>0–100 (100 = standstill).</summary>
        public float CongestionLevel { get; set; }

        public DateTime RecordedAt { get; set; }

        public bool IsActive { get; set; }

        public DateTime CreatedDateTime { get; set; }

        public Guid CreatedUserId { get; set; }

        // ── Added for ML dataset ─────────────────────────────────────────────

        public string? LocationName { get; set; }

        public float? CurrentSpeedKmh { get; set; }

        public float? FreeFlowSpeedKmh { get; set; }

        /// <summary>TomTom confidence 0–1.</summary>
        public float? Confidence { get; set; }

        /// <summary>Air temperature °C at time of sample (Open-Meteo).</summary>
        public float? Temperature { get; set; }

        public float? PrecipitationMm { get; set; }

        /// <summary>0 Clear, 1 Cloudy, 2 Rainy, 3 Stormy.</summary>
        public int? Weather { get; set; }

        /// <summary>Sri Lanka local hour 0–23.</summary>
        public int? LocalHour { get; set; }

        /// <summary>Sri Lanka local day of week 0 (Sun) – 6 (Sat).</summary>
        public int? LocalDayOfWeek { get; set; }

        /// <summary>tomtom | csv | legacy</summary>
        public string? Source { get; set; }
    }
}

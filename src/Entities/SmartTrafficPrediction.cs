using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SmartcityBackend.src.Entities
{
    /// <summary>
    /// A stored hourly forecast value. When live data for the same place/time
    /// arrives later, ActualCongestion is filled → used for accuracy (MAE) tracking.
    /// </summary>
    [Table("smart_TrafficPrediction")]
    public class SmartTrafficPrediction
    {
        [Key]
        public Guid PredictionId { get; set; }

        public Guid UserId { get; set; }

        public string LocationName { get; set; } = string.Empty;

        public decimal Latitude { get; set; }

        public decimal Longitude { get; set; }

        public DateTime TargetTimeUtc { get; set; }

        public float PredictedCongestion { get; set; }

        public float? ActualCongestion { get; set; }

        /// <summary>ml | historical | rule</summary>
        public string ModelUsed { get; set; } = string.Empty;

        public int Weather { get; set; }

        public float? Temperature { get; set; }

        public bool IsActive { get; set; } = true;

        public DateTime CreatedDateTime { get; set; }
    }
}

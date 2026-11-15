using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SmartcityBackend.src.Entities
{
    [Table("smart_EnergyUsage")]
    public class SmartEnergyUsage
    {
        [Key]
        public Guid EnergyUsageId { get; set; }

        public Guid UserId { get; set; }
        public float UsageUnits { get; set; }
        public float Temperature { get; set; }
        public int Occupants { get; set; }

        /// <summary>Predicted monthly bill in LKR (CEB domestic tariff; rows saved before Oct 2026 hold USD) — saved for history display.</summary>
        public float PredictedBill { get; set; }

        public DateTime RecordedAt { get; set; }
        public bool IsActive { get; set; } = true;
        public DateTime CreatedDateTime { get; set; }
    }
}
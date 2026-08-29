using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

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

        public DateTime RecordedAt { get; set; }

        public bool IsActive { get; set; }

        public DateTime CreatedDateTime { get; set; }
    }
}

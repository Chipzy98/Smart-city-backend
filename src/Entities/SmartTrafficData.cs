using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SmartcityBackend.src.Entities
{
    [Table("smart_TrafficData")]
    public class SmartTrafficData
    {
        [Key]
        public Guid TrafficDataId { get; set; }

        public decimal Latitude { get; set; }

        public decimal Longitude { get; set; }

        public float CongestionLevel { get; set; }

        public DateTime RecordedAt { get; set; }

        public bool IsActive { get; set; }

        public DateTime CreatedDateTime { get; set; }

        public Guid CreatedUserId { get; set; }
    }
}

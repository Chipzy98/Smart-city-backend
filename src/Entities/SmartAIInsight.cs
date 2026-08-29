using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SmartcityBackend.src.Entities
{
    [Table("smart_AIInsight")]
    public class SmartAIInsight
    {
        [Key]
        public Guid AIInsightId { get; set; }

        public string Type { get; set; } = string.Empty;

        public string Message { get; set; } = string.Empty;

        public DateTime CreatedDateTime { get; set; }

        public bool IsActive { get; set; }
    }
}

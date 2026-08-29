using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SmartcityBackend.src.Entities
{
    [Table("smart_WasteRecord")]
    public class SmartWasteRecord
    {
        [Key]
        public Guid WasteRecordId { get; set; }

        public Guid UserId { get; set; }

        public string ImageUrl { get; set; } = string.Empty;

        public string Category { get; set; } = string.Empty;

        public float Confidence { get; set; }

        public float Co2Saved { get; set; }

        public bool IsActive { get; set; }

        public DateTime CreatedDateTime { get; set; }
    }
}

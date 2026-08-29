using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SmartcityBackend.src.AI.Models
{
    public class WasteAIOutput
    {
        public string Category { get; set; } = string.Empty;

        public float Confidence { get; set; }

        public float Co2Saved { get; set; }
    }
}

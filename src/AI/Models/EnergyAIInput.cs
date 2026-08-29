using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SmartcityBackend.src.AI.Models
{
    public class EnergyAIInput
    {
        public float UsageUnits { get; set; }
        public float Temperature { get; set; }
        public float Occupants { get; set; }
    }
}

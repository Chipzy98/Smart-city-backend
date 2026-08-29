using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SmartcityBackend.src.AI.Models
{
    public class TrafficAIInput
    {
        public float Hour { get; set; }
        public float Day { get; set; }
        public float Weather { get; set; }
        public float VehicleCount { get; set; }
    }
}

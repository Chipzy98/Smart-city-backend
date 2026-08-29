using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SmartcityBackend.src.ViewModels
{
    public class EnergyPredictionRequestVM
    {
        public float UsageUnits { get; set; }
        public float Temperature { get; set; }
        public int Occupants { get; set; }
    }
}

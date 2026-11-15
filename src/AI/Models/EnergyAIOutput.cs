using Microsoft.ML.Data;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SmartcityBackend.src.AI.Models
{
    public class EnergyAIOutput
    {
        /// <summary>Monthly bill in LKR from the CEB domestic tariff (incl. SSCL).</summary>
        [ColumnName("Score")]
        public float PredictedBill { get; set; }

        public string Currency { get; set; } = "LKR";

        public string Tip { get; set; } = string.Empty;

        /// <summary>Units after the temperature adjustment — the units the bill is charged on.</summary>
        public float BilledUnits { get; set; }

        public float EnergyCharge { get; set; }

        public float FixedCharge { get; set; }

        public float Sscl { get; set; }

        public string Tariff { get; set; } = string.Empty;
    }
}

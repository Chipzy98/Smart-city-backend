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
        [ColumnName("Score")]
        public float PredictedBill { get; set; }

        public string Currency { get; set; } = "USD";

        public string Tip { get; set; } = string.Empty;
    }
}

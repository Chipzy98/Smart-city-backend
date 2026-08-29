using Microsoft.ML.Data;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SmartcityBackend.src.AI.Models
{
    public class TrafficAIOutput
    {
        [ColumnName("Score")]
        public float PredictedCongestionLevel { get; set; }

        public string Status { get; set; } = string.Empty;

        public string Suggestion { get; set; } = string.Empty;
    }
}

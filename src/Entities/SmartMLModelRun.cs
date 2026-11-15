using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SmartcityBackend.src.Entities
{
    /// <summary>History of ML model training runs with evaluation metrics.</summary>
    [Table("smart_MLModelRun")]
    public class SmartMLModelRun
    {
        [Key]
        public Guid ModelRunId { get; set; }

        public string ModelName { get; set; } = string.Empty;

        public string Algorithm { get; set; } = string.Empty;

        public int RowCount { get; set; }

        public int TrainRows { get; set; }

        public int TestRows { get; set; }

        /// <summary>Mean Absolute Error on the held-out test set (congestion points).</summary>
        public double MeanAbsoluteError { get; set; }

        public double RootMeanSquaredError { get; set; }

        public double RSquared { get; set; }

        public DateTime TrainedAt { get; set; }

        public Guid TrainedByUserId { get; set; }

        public bool IsActive { get; set; } = true;
    }
}

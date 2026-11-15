namespace SmartcityBackend.src.ViewModels
{
    public class TrafficForecastRequestVM
    {
        public string LocationName { get; set; } = string.Empty;
        public double Latitude { get; set; }
        public double Longitude { get; set; }

        /// <summary>How many hours ahead to forecast (3–24).</summary>
        public int Hours { get; set; } = 12;
    }
}

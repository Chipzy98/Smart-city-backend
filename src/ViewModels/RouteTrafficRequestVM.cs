namespace SmartcityBackend.src.ViewModels
{
    public class RouteTrafficRequestVM
    {
        public string OriginName { get; set; } = string.Empty;
        public double OriginLat { get; set; }
        public double OriginLng { get; set; }
        public string DestinationName { get; set; } = string.Empty;
        public double DestinationLat { get; set; }
        public double DestinationLng { get; set; }
        public int Weather { get; set; } = 0;
    }
}
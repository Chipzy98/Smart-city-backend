using System;

namespace SmartcityBackend.src
{
    public static class SmartCityTaskIds
    {
        public static readonly Guid SmartCityTraffic =
            Guid.Parse("A1000001-0001-4000-8000-000000000001");

        public static readonly Guid SmartCityWaste =
            Guid.Parse("A1000001-0001-4000-8000-000000000002");

        public static readonly Guid SmartCityEnergy =
            Guid.Parse("A1000001-0001-4000-8000-000000000003");

        public static readonly Guid SmartCityDashboard =
            Guid.Parse("A1000001-0001-4000-8000-000000000004");
    }
}
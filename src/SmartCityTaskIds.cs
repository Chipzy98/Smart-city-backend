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

        /// <summary>Personal (user) dashboard.</summary>
        public static readonly Guid SmartCityDashboard =
            Guid.Parse("A1000001-0001-4000-8000-000000000004");

        public static readonly Guid SmartCityAdminDashboard =
            Guid.Parse("A1000001-0001-4000-8000-000000000005");

        public static readonly Guid SmartCityManagerDashboard =
            Guid.Parse("A1000001-0001-4000-8000-000000000006");

        /// <summary>CSV training upload, ML model training, live data collection.</summary>
        public static readonly Guid SmartCityAITraining =
            Guid.Parse("A1000001-0001-4000-8000-000000000007");

        public static readonly Guid SmartCityClimate =
            Guid.Parse("A1000001-0001-4000-8000-000000000008");
    }
}

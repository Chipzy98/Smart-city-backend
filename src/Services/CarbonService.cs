using Microsoft.EntityFrameworkCore;
using SmartcityBackend.src.Data;
using SmartcityBackend.src.Helpers;

namespace SmartcityBackend.src.Services
{
    /// <summary>
    /// Personal carbon footprint & sustainability score built from the user's own data:
    ///   + Electricity : latest monthly kWh ÷ occupants × grid factor
    ///   + Travel      : route analyses (planned trips) km × average petrol car factor
    ///   − Waste       : CO₂ saved by correctly classified waste
    /// Score 0–100: 100 = net zero, 50 = Sri Lanka average per person, 0 = 2× average or more.
    /// </summary>
    public class CarbonService
    {
        /// <summary>Sri Lanka electricity carbon intensity 2025 — 274 gCO₂eq/kWh (lowcarbonpower.org).</summary>
        public const double GridKgPerKwh = 0.274;

        /// <summary>Average petrol car — 0.1645 kgCO₂e/km (UK BEIS/DESNZ conversion factors 2024).</summary>
        public const double CarKgPerKm = 0.1645;

        /// <summary>Sri Lanka CO₂ per person 2024 — 0.9 t/year (Our World in Data) ≈ 75 kg/month.</summary>
        public const double BenchmarkKgPerMonth = 900.0 / 12.0;

        private readonly SmartCityDbContext _db;

        public CarbonService(SmartCityDbContext db)
        {
            _db = db;
        }

        public async Task<object> GetFootprintAsync(Guid userId, CancellationToken ct = default)
        {
            var nowLocal = AppTime.NowLocal();
            var thisMonth = new DateTime(nowLocal.Year, nowLocal.Month, 1);
            var firstMonth = thisMonth.AddMonths(-5);
            var fromUtc = AppTime.ToUtc(firstMonth);

            var energy = await _db.SmartEnergyUsage
                .Where(x => x.IsActive && x.UserId == userId)
                .OrderBy(x => x.RecordedAt)
                .Select(x => new { x.RecordedAt, x.UsageUnits, x.Occupants })
                .ToListAsync(ct);

            var routes = await _db.SmartRouteTraffic
                .Where(x => x.IsActive && x.UserId == userId && x.CreatedDateTime >= fromUtc)
                .Select(x => new { x.CreatedDateTime, x.DistanceKm })
                .ToListAsync(ct);

            var waste = await _db.SmartWasteRecord
                .Where(x => x.IsActive && x.UserId == userId && x.CreatedDateTime >= fromUtc)
                .Select(x => new { x.CreatedDateTime, x.Co2Saved })
                .ToListAsync(ct);

            var months = Enumerable.Range(0, 6).Select(i =>
            {
                var start = firstMonth.AddMonths(i);
                var end = start.AddMonths(1);

                // Electricity: latest monthly usage entered up to the end of this month (carried forward)
                var e = energy.LastOrDefault(x => AppTime.ToLocal(x.RecordedAt) < end);
                var perPersonKwh = e == null ? 0 : e.UsageUnits / Math.Max(1, e.Occupants);
                var energyKg = perPersonKwh * GridKgPerKwh;

                var km = routes.Where(x => InMonth(x.CreatedDateTime, start, end)).Sum(x => (double)x.DistanceKm);
                var travelKg = km * CarKgPerKm;

                var savedKg = waste.Where(x => InMonth(x.CreatedDateTime, start, end)).Sum(x => (double)x.Co2Saved);

                var net = Math.Max(0, energyKg + travelKg - savedKg);
                return new MonthFootprint
                {
                    Month = start.ToString("yyyy-MM"),
                    Label = start.ToString("MMM"),
                    EnergyKg = Math.Round(energyKg, 2),
                    TravelKg = Math.Round(travelKg, 2),
                    WasteSavedKg = Math.Round(savedKg, 2),
                    NetKg = Math.Round(net, 2),
                    TravelKm = Math.Round(km, 1),
                    EnergyKwhPerPerson = Math.Round(perPersonKwh, 1),
                };
            }).ToList();

            var current = months[^1];
            var score = Score(current.NetKg);

            return new
            {
                month = current.Month,
                current,
                score,
                grade = score >= 80 ? "A" : score >= 65 ? "B" : score >= 50 ? "C" : score >= 35 ? "D" : "E",
                benchmarkKgPerMonth = Math.Round(BenchmarkKgPerMonth, 1),
                comparedToAveragePct = Math.Round(current.NetKg / BenchmarkKgPerMonth * 100, 0),
                history = months,
                tips = Tips(current),
                hasData = energy.Count > 0 || routes.Count > 0 || waste.Count > 0,
                factors = new[]
                {
                    new { name = "Electricity (Sri Lanka grid)", value = GridKgPerKwh, unit = "kg CO₂e / kWh", source = "lowcarbonpower.org — Sri Lanka 2025 (274 gCO₂eq/kWh)" },
                    new { name = "Average petrol car", value = CarKgPerKm, unit = "kg CO₂e / km", source = "UK BEIS/DESNZ GHG conversion factors 2024" },
                    new { name = "Sri Lanka average per person", value = Math.Round(BenchmarkKgPerMonth, 1), unit = "kg CO₂ / month", source = "Our World in Data — 0.9 t per person (2024)" },
                },
                notes = "Travel uses your route analyses as planned car trips. Electricity uses your latest monthly usage divided by occupants.",
            };
        }

        /// <summary>100 at zero net emissions, 50 at the national average, 0 at 2× average or more.</summary>
        public static int Score(double netKgPerMonth) =>
            (int)Math.Round(100 * Math.Clamp(1 - netKgPerMonth / (2 * BenchmarkKgPerMonth), 0, 1));

        private static bool InMonth(DateTime utc, DateTime startLocal, DateTime endLocal)
        {
            var local = AppTime.ToLocal(utc);
            return local >= startLocal && local < endLocal;
        }

        private static List<string> Tips(MonthFootprint m)
        {
            var tips = new List<string>();
            var biggest = m.EnergyKg >= m.TravelKg ? "energy" : "travel";

            if (m.EnergyKg == 0 && m.TravelKg == 0 && m.WasteSavedKg == 0)
            {
                tips.Add("Add an energy prediction, analyse a route or classify waste to calculate your footprint.");
                return tips;
            }

            if (biggest == "energy" && m.EnergyKg > 0)
            {
                tips.Add("Electricity is your biggest source — set A/C to 24–26 °C and switch to LED lighting.");
                if (m.EnergyKwhPerPerson > 100)
                    tips.Add("Your usage per person is high — check refrigerators, water heaters and irons (high-wattage appliances).");
                tips.Add("Rooftop solar can cut grid electricity emissions significantly in Sri Lanka's sunny climate.");
            }
            if (m.TravelKg > 0)
            {
                tips.Add(m.TravelKm > 300
                    ? "You plan a lot of car travel — try trains/buses or car-pooling for regular trips."
                    : "Combine errands into one trip and avoid peak-hour traffic to save fuel.");
            }
            tips.Add(m.WasteSavedKg > 0
                ? $"Great work — waste sorting saved {m.WasteSavedKg:F1} kg CO₂ this month. Keep recycling plastics and metals."
                : "Start classifying and recycling your waste — recyclables save about 1.8 kg CO₂ per item.");
            return tips;
        }

        public class MonthFootprint
        {
            public string Month { get; set; } = string.Empty;
            public string Label { get; set; } = string.Empty;
            public double EnergyKg { get; set; }
            public double TravelKg { get; set; }
            public double WasteSavedKg { get; set; }
            public double NetKg { get; set; }
            public double TravelKm { get; set; }
            public double EnergyKwhPerPerson { get; set; }
        }
    }
}

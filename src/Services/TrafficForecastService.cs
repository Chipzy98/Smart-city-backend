using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmartcityBackend.src.AI.Services;
using SmartcityBackend.src.Data;
using SmartcityBackend.src.Entities;
using SmartcityBackend.src.Helpers;
using SmartcityBackend.src.ViewModels;
using System.Text.Json;

namespace SmartcityBackend.src.Services
{
    /// <summary>
    /// Hybrid traffic flow forecast for a location, hour by hour.
    ///
    ///  1. Live TomTom flow at the point (current speed vs free-flow speed)
    ///  2. Open-Meteo hourly weather forecast for the same hours
    ///  3. Numbers: ML.NET model → else historical average for that hour near the point → else rule baseline
    ///  4. Nowcast correction: the gap between live congestion and the model's "now" value is
    ///     added to the next hours and fades out (half-life ≈ 2 h)
    ///  5. Claude writes a short explanation + travel suggestion (rule text if Claude is unavailable)
    ///  6. Future hours are stored in smart_TrafficPrediction and compared with live data later (MAE)
    /// </summary>
    public class TrafficForecastService
    {
        private readonly SmartCityDbContext _db;
        private readonly TomTomTrafficService _tomTom;
        private readonly OpenMeteoService _weather;
        private readonly TrafficMLService _ml;
        private readonly InsightService _insights;
        private readonly HttpClient _claude;
        private readonly ILogger<TrafficForecastService> _logger;

        private const double NearbyDegrees = 0.05;     // ≈ 5 km — historical profile radius
        private const double SamePlaceDegrees = 0.01;  // ≈ 1 km — accuracy matching radius

        public TrafficForecastService(
            SmartCityDbContext db,
            TomTomTrafficService tomTom,
            OpenMeteoService weather,
            TrafficMLService ml,
            InsightService insights,
            IHttpClientFactory httpClientFactory,
            ILogger<TrafficForecastService> logger)
        {
            _db = db;
            _tomTom = tomTom;
            _weather = weather;
            _ml = ml;
            _insights = insights;
            _claude = httpClientFactory.CreateClient("Anthropic");
            _logger = logger;
        }

        public async Task<object> ForecastAsync(Guid userId, TrafficForecastRequestVM req, CancellationToken ct = default)
        {
            var hours = Math.Clamp(req.Hours, 3, 24);
            var lat = req.Latitude;
            var lng = req.Longitude;
            var locationName = string.IsNullOrWhiteSpace(req.LocationName) ? $"{lat:F4}, {lng:F4}" : req.LocationName.Trim();
            var nowLocal = AppTime.NowLocal();

            // 1) Live flow + 2) weather (in parallel)
            var liveTask = _tomTom.FetchFlowAsync(lat, lng, ct);
            var weatherTask = SafeWeatherAsync(lat, lng, hours + 1, ct);
            await Task.WhenAll(liveTask, weatherTask);
            var live = liveTask.Result;
            var wx = weatherTask.Result;

            // Historical profile near this location
            var profile = await LoadHistoricalProfileAsync(lat, lng, ct);
            var freeFlow = live is { FreeFlowSpeedKmh: > 0 } ? live.FreeFlowSpeedKmh : profile.FreeFlowSpeed ?? 50f;

            // 3) Raw hourly predictions
            var mlReady = _ml.IsTrained;
            var modelUsed = mlReady ? "ml" : profile.HasData ? "historical" : "rule";
            var slots = new List<ForecastSlot>();
            var hourStart = new DateTime(nowLocal.Year, nowLocal.Month, nowLocal.Day, nowLocal.Hour, 0, 0);

            for (var i = 0; i <= hours; i++)
            {
                var target = i == 0 ? nowLocal : hourStart.AddHours(i);
                var hw = wx?.Hourly.FirstOrDefault(h => h.Time == new DateTime(target.Year, target.Month, target.Day, target.Hour, 0, 0));
                var temp = (float)(hw?.Temperature ?? wx?.Current?.Temperature ?? 28);
                var rain = (float)(hw?.PrecipitationMm ?? (i == 0 ? wx?.Current?.PrecipitationMm : null) ?? 0);
                var code = hw?.WeatherCode ?? wx?.Current?.WeatherCode;
                var weather = OpenMeteoService.ToWeatherCategory(code, rain);
                var dow = (int)target.DayOfWeek;

                float value;
                if (mlReady)
                {
                    value = _ml.Predict(TrafficModelInput.Create(target.Hour, dow, weather, temp, rain, lat, lng, freeFlow))
                            ?? RuleBaseline(target.Hour, dow, weather);
                }
                else if (profile.ByHour.TryGetValue(target.Hour, out var avg))
                {
                    value = avg + WeatherPenalty(weather);
                }
                else
                {
                    value = RuleBaseline(target.Hour, dow, weather);
                }

                slots.Add(new ForecastSlot
                {
                    TimeLocal = target,
                    Hour = target.Hour,
                    Raw = Math.Clamp(value, 0f, 100f),
                    Temperature = temp,
                    PrecipitationMm = rain,
                    PrecipitationProbability = (float?)hw?.PrecipitationProbability,
                    Weather = weather,
                    WeatherCode = code,
                });
            }

            // 4) Nowcast correction using the live observation
            if (live != null && slots.Count > 0)
            {
                var bias = live.CongestionLevel - slots[0].Raw;
                for (var i = 0; i < slots.Count; i++)
                {
                    var adjusted = slots[i].Raw + bias * (float)Math.Pow(0.5, i / 2.0);
                    slots[i].Value = Math.Clamp(adjusted, 0f, 100f);
                }
                slots[0].Value = live.CongestionLevel;
                slots[0].IsLive = true;
            }
            else
            {
                foreach (var s in slots) s.Value = s.Raw;
            }

            var future = slots.Skip(1).ToList();
            var peak = future.OrderByDescending(s => s.Value).First();
            var best = future.OrderBy(s => s.Value).ThenBy(s => s.TimeLocal).First();

            // 5) Explanation
            var (summary, suggestion, explanationSource) =
                await ExplainAsync(locationName, live, slots, peak, best, modelUsed, ct);

            // 6) Persist predictions + fill accuracy for older predictions at this place
            await SavePredictionsAsync(userId, locationName, lat, lng, future, modelUsed, ct);
            if (live != null) await FillActualsAsync(lat, lng, live.CongestionLevel, ct);

            if (peak.Value >= 75)
            {
                await _insights.AddAsync("traffic",
                    $"Heavy congestion ({peak.Value:F0}/100) expected near {locationName} around {peak.TimeLocal:HH:mm}. " +
                    $"Best time to travel: {best.TimeLocal:HH:mm}.", TimeSpan.FromHours(3), ct);
            }

            var metrics = _ml.Metrics;
            return new
            {
                locationName,
                latitude = lat,
                longitude = lng,
                generatedAt = AppTime.ToIsoLocal(nowLocal),
                modelUsed,
                model = mlReady && metrics != null
                    ? new
                    {
                        algorithm = metrics.Algorithm,
                        rows = metrics.Rows,
                        mae = metrics.MeanAbsoluteError,
                        rmse = metrics.RootMeanSquaredError,
                        r2 = metrics.RSquared,
                        trainedAt = metrics.TrainedAt,
                    }
                    : null,
                live = live == null ? null : new
                {
                    currentSpeedKmh = Math.Round(live.CurrentSpeedKmh, 1),
                    freeFlowSpeedKmh = Math.Round(live.FreeFlowSpeedKmh, 1),
                    congestionLevel = Math.Round(live.CongestionLevel, 1),
                    congestionStatus = live.CongestionStatus,
                    confidence = Math.Round(live.Confidence, 2),
                },
                weatherNow = wx?.Current == null ? null : new
                {
                    temperature = wx.Current.Temperature,
                    precipitationMm = wx.Current.PrecipitationMm,
                    weather = OpenMeteoService.ToWeatherCategory(wx.Current.WeatherCode, wx.Current.PrecipitationMm),
                    description = OpenMeteoService.Describe(wx.Current.WeatherCode),
                },
                forecast = slots.Select(s => new
                {
                    time = AppTime.ToIsoLocal(s.TimeLocal),
                    hour = s.Hour,
                    congestionLevel = Math.Round(s.Value, 1),
                    status = TrafficLevels.Status(s.Value),
                    temperature = Math.Round(s.Temperature, 1),
                    precipitationMm = Math.Round(s.PrecipitationMm, 1),
                    precipitationProbability = s.PrecipitationProbability,
                    weather = s.Weather,
                    weatherDescription = OpenMeteoService.Describe(s.WeatherCode),
                    isLive = s.IsLive,
                }),
                peak = new { time = AppTime.ToIsoLocal(peak.TimeLocal), congestionLevel = Math.Round(peak.Value, 1) },
                best = new { time = AppTime.ToIsoLocal(best.TimeLocal), congestionLevel = Math.Round(best.Value, 1) },
                summary,
                suggestion,
                explanationSource,
            };
        }

        // ── Model status + accuracy (AI Training page) ─────────────────────

        public async Task<object> GetModelStatusAsync(CancellationToken ct = default)
        {
            var metrics = _ml.Metrics;

            var bySource = await _db.SmartTrafficData
                .Where(x => x.IsActive)
                .GroupBy(x => x.Source ?? "legacy")
                .Select(g => new { source = g.Key, rows = g.Count() })
                .ToListAsync(ct);

            var newestSample = await _db.SmartTrafficData
                .Where(x => x.IsActive)
                .OrderByDescending(x => x.RecordedAt)
                .Select(x => (DateTime?)x.RecordedAt)
                .FirstOrDefaultAsync(ct);

            var evaluated = await _db.SmartTrafficPrediction
                .Where(x => x.IsActive && x.ActualCongestion != null)
                .OrderByDescending(x => x.TargetTimeUtc)
                .Take(2000)
                .Select(x => new { x.ModelUsed, x.PredictedCongestion, Actual = x.ActualCongestion!.Value })
                .ToListAsync(ct);

            var runs = await _db.SmartMLModelRun
                .Where(x => x.IsActive && x.ModelName == TrafficMLService.ModelName)
                .OrderByDescending(x => x.TrainedAt)
                .Take(10)
                .Select(x => new
                {
                    x.TrainedAt, x.RowCount, mae = x.MeanAbsoluteError,
                    rmse = x.RootMeanSquaredError, r2 = x.RSquared,
                })
                .ToListAsync(ct);

            return new
            {
                trained = _ml.IsTrained,
                minRows = TrafficMLService.MinRows,
                algorithm = TrafficMLService.Algorithm,
                features = TrafficMLService.FeatureColumns,
                metrics,
                dataset = new
                {
                    totalRows = bySource.Sum(x => x.rows),
                    bySource,
                    newestSampleAt = newestSample,
                },
                collector = new
                {
                    schedule = _tomTom.Provider == "google"
                        ? $"Every {(int.TryParse(Environment.GetEnvironmentVariable("GOOGLE_COLLECT_EVERY_HOURS"), out var eh) && eh > 0 ? eh : 2)} hours (Google)"
                        : "Every 30 minutes (TomTom)",
                    points = TomTomTrafficService.SriLankaPoints.Select(p => p.Name),
                    tomTomConfigured = _tomTom.IsConfigured,   // true when any traffic key is set
                    provider = _tomTom.Provider,
                    googleUsedToday = GoogleTrafficService.UsedToday,
                    googleDailyLimit = GoogleTrafficService.DailyLimit,
                },
                liveAccuracy = new
                {
                    evaluatedPredictions = evaluated.Count,
                    mae = evaluated.Count > 0
                        ? Math.Round(evaluated.Average(e => Math.Abs(e.PredictedCongestion - e.Actual)), 2)
                        : (double?)null,
                    byModel = evaluated
                        .GroupBy(e => e.ModelUsed)
                        .Select(g => new
                        {
                            model = g.Key,
                            count = g.Count(),
                            mae = Math.Round(g.Average(e => Math.Abs(e.PredictedCongestion - e.Actual)), 2),
                        }),
                },
                trainingHistory = runs,
            };
        }

        /// <summary>Called by the collector after saving live points.</summary>
        public async Task FillActualsForPointsAsync(IEnumerable<TrafficFlowPoint> points, CancellationToken ct = default)
        {
            foreach (var p in points)
                await FillActualsAsync(p.Lat, p.Lng, p.CongestionLevel, ct);
        }

        // ── Estimated "now" for the monitored roads (map fallback) ──────────

        /// <summary>
        /// Used by the live map when TomTom has no data (e.g. outside its traffic coverage):
        /// estimates the current congestion at each monitored road with the same chain as the
        /// forecast — ML model → historical hourly average → rule baseline — and current weather.
        /// </summary>
        public async Task<(List<TrafficFlowPoint> Points, string Source)> EstimateNowAsync(CancellationToken ct = default)
        {
            var nowLocal = AppTime.NowLocal();
            var dow = (int)nowLocal.DayOfWeek;
            var mlReady = _ml.IsTrained;
            var sources = new HashSet<string>();
            var points = new List<TrafficFlowPoint>();

            foreach (var (name, lat, lng) in TomTomTrafficService.SriLankaPoints)
            {
                var wx = await SafeWeatherAsync(lat, lng, 1, ct);
                var temp = (float)(wx?.Current?.Temperature ?? 28);
                var rain = (float)(wx?.Current?.PrecipitationMm ?? 0);
                var weather = OpenMeteoService.ToWeatherCategory(wx?.Current?.WeatherCode, rain);

                var profile = await LoadHistoricalProfileAsync(lat, lng, ct);
                var freeFlow = profile.FreeFlowSpeed ?? 50f;

                float value; string source;
                var predicted = mlReady
                    ? _ml.Predict(TrafficModelInput.Create(nowLocal.Hour, dow, weather, temp, rain, lat, lng, freeFlow))
                    : null;
                if (predicted != null) { value = predicted.Value; source = "ml"; }
                else if (profile.ByHour.TryGetValue(nowLocal.Hour, out var avg)) { value = avg + WeatherPenalty(weather); source = "historical"; }
                else { value = RuleBaseline(nowLocal.Hour, dow, weather); source = "rule"; }

                value = Math.Clamp(value, 0f, 100f);
                sources.Add(source);
                points.Add(new TrafficFlowPoint
                {
                    LocationName = name,
                    Lat = lat,
                    Lng = lng,
                    FreeFlowSpeedKmh = freeFlow,
                    CurrentSpeedKmh = freeFlow * (1f - value / 100f),
                    CongestionLevel = value,
                    Confidence = 0f,
                    Temperature = temp,
                    PrecipitationMm = rain,
                    Weather = weather,
                    FetchedAt = DateTime.UtcNow,
                });
            }

            var overall = sources.Contains("ml") ? "ml" : sources.Contains("historical") ? "historical" : "rule";
            return (points, overall);
        }

        // ── Internals ──────────────────────────────────────────────────────

        private async Task<WeatherForecast?> SafeWeatherAsync(double lat, double lng, int hours, CancellationToken ct)
        {
            try { return await _weather.GetForecastAsync(lat, lng, hours, 2, ct); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Forecast] Weather unavailable.");
                return null;
            }
        }

        private async Task<HistoricalProfile> LoadHistoricalProfileAsync(double lat, double lng, CancellationToken ct)
        {
            var minLat = (decimal)(lat - NearbyDegrees);
            var maxLat = (decimal)(lat + NearbyDegrees);
            var minLng = (decimal)(lng - NearbyDegrees);
            var maxLng = (decimal)(lng + NearbyDegrees);
            var since = DateTime.UtcNow.AddDays(-60);

            var rows = await _db.SmartTrafficData
                .Where(x => x.IsActive && x.RecordedAt >= since
                            && x.Latitude >= minLat && x.Latitude <= maxLat
                            && x.Longitude >= minLng && x.Longitude <= maxLng)
                .OrderByDescending(x => x.RecordedAt)
                .Take(5000)
                .Select(x => new { x.LocalHour, x.RecordedAt, x.CongestionLevel, x.FreeFlowSpeedKmh })
                .ToListAsync(ct);

            var profile = new HistoricalProfile();
            if (rows.Count < 24) return profile;

            profile.HasData = true;
            profile.ByHour = rows
                .GroupBy(r => r.LocalHour ?? AppTime.ToLocal(r.RecordedAt).Hour)
                .ToDictionary(g => g.Key, g => g.Average(r => r.CongestionLevel));

            var ff = rows.Where(r => r.FreeFlowSpeedKmh is > 0).Select(r => r.FreeFlowSpeedKmh!.Value).ToList();
            profile.FreeFlowSpeed = ff.Count > 0 ? ff.Average() : null;
            return profile;
        }

        /// <summary>Simple daily congestion shape for Sri Lankan urban roads (used only without data).</summary>
        public static float RuleBaseline(int hour, int dayOfWeek, int weather)
        {
            float baseLevel = hour switch
            {
                <= 5 => 10,
                6 => 25,
                >= 7 and <= 9 => 65,
                >= 10 and <= 15 => 40,
                16 => 50,
                >= 17 and <= 19 => 70,
                20 or 21 => 35,
                _ => 20,
            };
            if (AppTime.IsWeekend(dayOfWeek)) baseLevel *= 0.7f;
            return Math.Clamp(baseLevel + WeatherPenalty(weather), 0f, 100f);
        }

        private static float WeatherPenalty(int weather) => weather switch
        {
            1 => 3f,
            2 => 10f,
            3 => 18f,
            _ => 0f,
        };

        private async Task<(string Summary, string Suggestion, string Source)> ExplainAsync(
            string location, TrafficFlowPoint? live, List<ForecastSlot> slots,
            ForecastSlot peak, ForecastSlot best, string modelUsed, CancellationToken ct)
        {
            var table = string.Join("\n", slots.Select(s =>
                $"{s.TimeLocal:HH:mm} congestion={s.Value:F0} weather={s.Weather} temp={s.Temperature:F0}C rain={s.PrecipitationMm:F1}mm"));

            var prompt =
                "You are a Sri Lanka smart city traffic assistant.\n" +
                "Explain this hourly congestion forecast (0 = free flow, 100 = standstill) to a driver.\n" +
                "Weather scale: 0 clear, 1 cloudy, 2 rain, 3 storm.\n\n" +
                $"Location: {location}\n" +
                (live != null
                    ? $"Live now: {live.CurrentSpeedKmh:F0} km/h vs free-flow {live.FreeFlowSpeedKmh:F0} km/h (congestion {live.CongestionLevel:F0})\n"
                    : "Live data: not available\n") +
                $"Prediction source: {modelUsed}\n\n{table}\n\n" +
                "Reply ONLY with JSON (no markdown):\n" +
                "{ \"summary\": \"<2 sentences about how traffic will change and why>\", " +
                "\"suggestion\": \"<1 practical sentence: best time to leave or what to avoid>\" }";

            try
            {
                var text = await ClaudeApiClient.SendPromptAsync(_claude, prompt, 300, ct);
                using var doc = JsonDocument.Parse(ClaudeApiClient.CleanJson(text));
                var summary = doc.RootElement.GetProperty("summary").GetString();
                var suggestion = doc.RootElement.GetProperty("suggestion").GetString();
                if (!string.IsNullOrWhiteSpace(summary) && !string.IsNullOrWhiteSpace(suggestion))
                    return (summary!, suggestion!, "claude");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Forecast] Claude explanation failed — using rule text.");
            }

            var nowLevel = slots[0].Value;
            var trend = peak.Value - nowLevel > 10 ? "is expected to get heavier" :
                        nowLevel - best.Value > 10 ? "is expected to ease" : "should stay fairly steady";
            var rainNote = slots.Any(s => s.Weather >= 2) ? " Rain in the forecast may slow traffic further." : "";
            return (
                $"Traffic near {location} {trend} over the next hours, peaking around {peak.TimeLocal:HH:mm} ({peak.Value:F0}/100).{rainNote}",
                best.Value + 10 < nowLevel
                    ? $"If you can wait, leaving around {best.TimeLocal:HH:mm} should be smoother ({best.Value:F0}/100)."
                    : $"Now is a reasonable time to travel; avoid {peak.TimeLocal:HH:mm} if possible.",
                "rule");
        }

        private async Task SavePredictionsAsync(
            Guid userId, string locationName, double lat, double lng,
            List<ForecastSlot> future, string modelUsed, CancellationToken ct)
        {
            try
            {
                var now = DateTime.UtcNow;
                _db.SmartTrafficPrediction.AddRange(future.Select(s => new SmartTrafficPrediction
                {
                    PredictionId = Guid.NewGuid(),
                    UserId = userId,
                    LocationName = locationName.Length > 200 ? locationName[..200] : locationName,
                    Latitude = (decimal)Math.Round(lat, 6),
                    Longitude = (decimal)Math.Round(lng, 6),
                    TargetTimeUtc = AppTime.ToUtc(s.TimeLocal),
                    PredictedCongestion = (float)Math.Round(s.Value, 1),
                    ModelUsed = modelUsed,
                    Weather = s.Weather,
                    Temperature = s.Temperature,
                    IsActive = true,
                    CreatedDateTime = now,
                }));
                await _db.SaveChangesAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Forecast] Could not save predictions.");
            }
        }

        private async Task FillActualsAsync(double lat, double lng, float actual, CancellationToken ct)
        {
            try
            {
                var now = DateTime.UtcNow;
                var from = now.AddMinutes(-30);
                var to = now.AddMinutes(30);
                var minLat = (decimal)(lat - SamePlaceDegrees);
                var maxLat = (decimal)(lat + SamePlaceDegrees);
                var minLng = (decimal)(lng - SamePlaceDegrees);
                var maxLng = (decimal)(lng + SamePlaceDegrees);

                var pending = await _db.SmartTrafficPrediction
                    .Where(x => x.IsActive && x.ActualCongestion == null
                                && x.TargetTimeUtc >= from && x.TargetTimeUtc <= to
                                && x.Latitude >= minLat && x.Latitude <= maxLat
                                && x.Longitude >= minLng && x.Longitude <= maxLng)
                    .ToListAsync(ct);

                if (pending.Count == 0) return;
                foreach (var p in pending) p.ActualCongestion = actual;
                await _db.SaveChangesAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Forecast] Could not update prediction accuracy.");
            }
        }

        private sealed class ForecastSlot
        {
            public DateTime TimeLocal { get; set; }
            public int Hour { get; set; }
            public float Raw { get; set; }
            public float Value { get; set; }
            public float Temperature { get; set; }
            public float PrecipitationMm { get; set; }
            public float? PrecipitationProbability { get; set; }
            public int Weather { get; set; }
            public int? WeatherCode { get; set; }
            public bool IsLive { get; set; }
        }

        private sealed class HistoricalProfile
        {
            public bool HasData { get; set; }
            public Dictionary<int, float> ByHour { get; set; } = new();
            public float? FreeFlowSpeed { get; set; }
        }
    }
}

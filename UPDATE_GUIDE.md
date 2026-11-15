# EcoCity Smart City — Update Guide (v2)

This update fixes the bugs found in the review, adds a **hybrid ML traffic flow
forecast**, and adds **climate features** (weather, air quality, alerts, carbon footprint).

---

## 1. Setup steps (do these in order)

### 1.1 Database
Run `SmartcityBackend/Database/001_traffic_ml_climate.sql` in SSMS on your database.
It is safe to run more than once. It:
- adds ML dataset columns to `smart_TrafficData`
- creates `smart_TrafficPrediction` (forecast accuracy) and `smart_MLModelRun` (training history)
- adds indexes
- seeds the demo users shown on the login page
  (`admin@ecocity.com / Admin@123`, `manager@ecocity.com / Manager@123`, `user@ecocity.com / User@123`)

### 1.2 Backend (`SmartcityBackend`)
1. Copy `local.settings.example.json` → `local.settings.json` (or edit your existing one) and set:
   | Key | Notes |
   |---|---|
   | `DefaultConnection` | SQL Server connection string |
   | `ANTHROPIC_API_KEY` | Real key — without it every AI feature uses the rule-based fallback |
   | `ANTHROPIC_MODEL` | Optional, default `claude-sonnet-4-6` |
   | `TOMTOM_API_KEY` | Live traffic + data collector |
   | `JWT_SECRET` | **Required, at least 32 characters** |
   | `TRAFFIC_COLLECTOR_ENABLED` | `true` / `false` (pause the 30-min collector) |
   | `APP_DATA_DIR` | Optional folder for ML model + training files |
2. New NuGet package: `Microsoft.Azure.Functions.Worker.Extensions.Timer 4.3.1` (already in the .csproj — just restore/build).
3. Timer functions need storage: `AzureWebJobsStorage = UseDevelopmentStorage=true` + **Azurite**
   (Visual Studio 2022 starts Azurite automatically for Functions projects).
4. Run on port 7261 as before.

> Existing logins become invalid once (tokens now carry issuer/audience) — just log in again.

### 1.3 Frontend
1. `npm install` (removed: `package-name` junk dependency, `leaflet`, `@types/leaflet`, `@types/google.maps`).
2. Copy `.env.example` → `.env.local`:
   - `SMART_CITY_API_URL=http://localhost:7261` (server-side only now)
   - `NEXT_PUBLIC_MAPBOX_TOKEN`, `NEXT_PUBLIC_TOMTOM_TOKEN`
   - `NEXT_PUBLIC_BACKEND_URL` and `NEXT_PUBLIC_GOOGLE_MAPS_API_KEY` are **no longer used**.
3. `npm run dev`

### 1.4 Train the traffic model (first time)
1. Log in as **admin** or **manager** → **AI Training**.
2. Traffic Data → *Download sample dataset* → upload it → **Train AI** (rows go into the ML dataset).
3. Click **Train model now**. You will see R², MAE, RMSE and the improvement over the baseline.
4. Real TomTom data is collected every 30 minutes; the model retrains every night at 02:00 (Sri Lanka).
   Once you have enough real data, upload a new CSV or just retrain — the CSV import is replaced on each upload.

> The sample CSV is **synthetic** (generated with realistic rush-hour patterns) — say so in your report.
> For comparison, a gradient-boosted tree model (sklearn, same features) on the sample scores
> R² ≈ 0.94, MAE ≈ 4.3 vs. a baseline MAE ≈ 17. Real TomTom data will be noisier.

---

## 2. Bug fixes

| # | Bug | Fix |
|---|---|---|
| 1 | User dashboard showed 0 everywhere; "Users" always 0 | All dashboards return one shape (`totalUsers`, `totalWasteRecords`…); role-specific cards |
| 2 | Route rush-hour logic used UTC (5.5 h wrong) | `AppTime` helper converts to Asia/Colombo everywhere |
| 3 | History stats used only the latest 10 rows | Backend returns `stats` over **all** records (waste, energy) |
| 4 | Waste upload only sent the file name | Image is resized in the browser (≤1568 px JPEG) and sent as base64 to Claude vision |
| 5 | AI routes never drawn on the map; Recent Routes empty | Selected route drawn (OSRM-snapped, purple); route history endpoint wired |
| 6 | Traffic "DB Records" empty | Collector + map now fill `smart_TrafficData`; list shows location names |
| 7 | Dashboard chart & insight hard-coded; `smart_AIInsight` never written | Real 7-day trends; traffic & climate insights saved automatically |
| 8 | Anyone could register as admin | Public registration always creates `user` |
| 9 | No role checks in the API | `SmartCityPermissions` matrix (TaskId + Action → roles), 401 vs 403 |
| 10 | No frontend route protection; guests saw admin menu | `ProtectedRoute` guard (token expiry + roles), 401 → login, sidebar default role `user` |
| 11 | Mixed `AuthorizationLevel.Function`/`Anonymous` (401 after deploy) | All `Anonymous` (JWT does the auth) |
| 12 | Training files lost on redeploy | `AppConfig.DataDir` (APP_DATA_DIR / Azure %HOME%) |
| 13 | AI-training page used port 7071 | All calls go through the Next.js proxy |
| 14 | Waste used sync-over-async; TomTom list not thread-safe | `ClassifyAsync`; `ConcurrentBag` |
| 15 | TomTom quota burned by browser auto-refresh (17 calls/min) | Map reads backend data; backend caches TomTom for 10 min |
| — | Missing API key crashed the AI services at startup | Missing key → rule-based fallback |
| — | Login email case-sensitive | Emails normalised to lower case |
| — | Dashboard hydration error (React #418) | localStorage read via `useSyncExternalStore` |

Cleanup: removed `TrafficMap.tsx` (Leaflet), `Helper/Common.ts`, `utils/useAuth.ts`, `google.d.ts`,
the 12 duplicated proxy route files (replaced by one catch-all), unused ML folder. `GoogleTrafficMap` → `LiveTrafficMap`.

---

## 3. Hybrid ML Traffic Flow Prediction

```
TomTom live flow ─┐                         ┌─ ML.NET FastTree model (if trained)
Open-Meteo weather ├─► hourly features ─────►├─ else historical hourly average (≈5 km)
Historical data  ─┘                         └─ else rule baseline
                          │
                nowcast correction: (live − model now) added, fades out (half-life 2 h)
                          │
          Claude: 2-sentence explanation + best time to travel (rule text fallback)
                          │
      stored in smart_TrafficPrediction → compared with live data later (real MAE)
```

- **Features:** hour (sin/cos), day of week, weekend, rush hour, weather (0–3), temperature, rain,
  latitude, longitude, free-flow speed. **Label:** congestion 0–100 = (1 − current/free-flow speed) × 100.
- **Evaluation:** 80/20 train-test split → MAE, RMSE, R² + baseline MAE; every run saved in `smart_MLModelRun`.
- **Data collection:** 10 Sri Lankan road points every 30 min ≈ 14,400 TomTom calls/month
  (free tier: 20,000 flow-segment requests/month).

New endpoints

| Method | Route | Role |
|---|---|---|
| POST | `/api/smartcity/traffic/forecast` | all |
| POST | `/api/smartcity/traffic/predict` (what-if, uses ML) | all |
| GET  | `/api/smartcity/traffic/realtime?refresh=` | all |
| GET  | `/api/smartcity/ai/traffic/model` | manager, admin |
| POST | `/api/smartcity/ai/traffic/train-model` | manager, admin |
| POST | `/api/smartcity/ai/traffic/collect` | manager, admin |
| Timer | `CollectTrafficData` (every 30 min), `RetrainTrafficModel` (02:00 SL) | — |

---

## 4. Climate features (Open-Meteo, free, no key)

| Feature | Details |
|---|---|
| Weather dashboard | 8 cities (Colombo, Kandy, Galle, Jaffna, Trincomalee, Anuradhapura, Nuwara Eliya, Ratnapura), live + 7-day forecast |
| Air quality | US AQI, PM2.5, PM10 map + 24 h AQI forecast + health advice |
| Climate alerts | Heat (NOAA heat-index bands on feels-like temp), rain ≥ 50/100/150 mm, thunderstorms, wind gusts ≥ 60/90 km/h, AQI > 100/150/200, UV ≥ 8/11 → dashboard banner + insights |
| Carbon footprint | Electricity (0.274 kg/kWh, Sri Lanka 2025) + travel (0.1645 kg/km, avg petrol car) − recycling savings; score 0–100 vs. Sri Lanka average (0.9 t/person/yr) |
| Integration | Energy page "Use live temperature"; traffic forecast uses hourly weather |

Endpoints: `GET /api/smartcity/climate/overview | forecast?lat&lng&name | current?lat&lng | carbon`

Sources for factors: lowcarbonpower.org (Sri Lanka 2025), UK DESNZ/BEIS GHG conversion factors 2024,
Our World in Data (Sri Lanka CO₂ per person 2024), TomTom pricing page, Open-Meteo docs.

---

## 5. What was verified (and what was not)

- Backend source compiled with .NET 8 against signature stubs of the NuGet packages
  (NuGet itself was not reachable from the build machine) + unit checks for weather parsing,
  alert rules, Sri Lanka time conversion, carbon score and the permission matrix — all passed.
- Frontend: `tsc`, `eslint` and `next build` pass; every page was rendered against a mock backend.
- **Not run here:** the real Azure Functions host, SQL Server, ML.NET training, and live calls to
  TomTom / Open-Meteo / Claude. Build in Visual Studio and run the SQL script first.

---

## 6. Live traffic from Google (v7)

TomTom has no real-time traffic coverage in Sri Lanka (every request returns
`400 Point too far from nearest existing segment`). Live traffic now comes from the
**Google Routes API** (`computeRoutes`, `TRAFFIC_AWARE`); TomTom stays as a fallback.

| Setting | Default | Notes |
|---|---|---|
| `GOOGLE_MAPS_API_KEY` | — | Key with **Routes API** enabled + billing on |
| `GOOGLE_ROUTES_DAILY_LIMIT` | 160 | Requests per day (≈ 4,960 / month, under the 5,000 free "Compute Routes Pro" cap) |
| `GOOGLE_COLLECT_EVERY_HOURS` | 2 | Collector runs every N hours with Google (10 roads × 12 = 120 / day) |

How it works: each monitored road has a short segment (≈1–2 km). Google returns `duration`
(with traffic) and `staticDuration` (without). Speeds = distance / time and
congestion = (1 − staticDuration / duration) × 100 — the same formula as TomTom.
Rows are stored with `Source = 'google'`. With no live data at all, the map shows model estimates.

Also in this version: model files are stored in `%LOCALAPPDATA%\EcoCitySmartCity` (not in `bin`,
which made the Functions host restart), and `Database/002_fix_float_columns.sql` fixes the
"Double to Single" cast error on older databases.

## 7. Real air-quality stations — WAQI (v8)

| Setting | Default | Notes |
|---|---|---|
| `WAQI_API_TOKEN` | — | Free token from aqicn.org/data-platform/token |
| `WAQI_MAX_DISTANCE_KM` | 25 | Use a station only if it is this close to the city |

For each city the nearest WAQI ground station is used when it is within the distance and its
reading is under 6 hours old; otherwise the Open-Meteo / CAMS model AQI is shown. The page labels
every value as "Station" or "Model estimate" and shows the required WAQI attribution.
Readings are cached for 30 minutes.

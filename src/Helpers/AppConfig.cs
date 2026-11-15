namespace SmartcityBackend.src.Helpers
{
    /// <summary>Central place for environment-driven settings.</summary>
    public static class AppConfig
    {
        /// <summary>Claude model used by all AI services. Override with ANTHROPIC_MODEL.</summary>
        public static string AnthropicModel =>
            Environment.GetEnvironmentVariable("ANTHROPIC_MODEL") is { Length: > 0 } m
                ? m
                : "claude-sonnet-4-6";

        public static string? AnthropicApiKey => Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");

        public static string? TomTomApiKey => Environment.GetEnvironmentVariable("TOMTOM_API_KEY");
        public static string? GoogleMapsApiKey => Environment.GetEnvironmentVariable("GOOGLE_MAPS_API_KEY");
        public static string? WaqiApiToken => Environment.GetEnvironmentVariable("WAQI_API_TOKEN");

        /// <summary>
        /// Persistent folder for training context files and ML models.
        /// Priority: APP_DATA_DIR → Azure App Service %HOME%\data\smartcity (survives redeploys)
        /// → %LOCALAPPDATA%\EcoCitySmartCity (development).
        /// </summary>
        public static string DataDir
        {
            get
            {
                var configured = Environment.GetEnvironmentVariable("APP_DATA_DIR");
                string dir;

                if (!string.IsNullOrWhiteSpace(configured))
                    dir = configured;
                else if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WEBSITE_INSTANCE_ID"))
                         && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("HOME")))
                    dir = Path.Combine(Environment.GetEnvironmentVariable("HOME")!, "data", "smartcity");
                else
                    // NOT inside the build output (bin): the Functions host watches that folder and
                    // restarts when a file/folder appears there, which breaks the isolated worker.
                    dir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "EcoCitySmartCity");

                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        public static string TrainingDir
        {
            get
            {
                var dir = Path.Combine(DataDir, "training_data");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        public static string ModelsDir
        {
            get
            {
                var dir = Path.Combine(DataDir, "models");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        public static string TrainingFile(string module) =>
            Path.Combine(TrainingDir, $"{module}_context.json");
    }
}

using Microsoft.EntityFrameworkCore;
using SmartcityBackend.src.Entities;

namespace SmartcityBackend.src.Data
{
    public class SmartCityDbContext : DbContext
    {
        public SmartCityDbContext(DbContextOptions<SmartCityDbContext> options)
            : base(options) { }

        public DbSet<SmartUser> SmartUser { get; set; }
        public DbSet<SmartTrafficData> SmartTrafficData { get; set; }
        public DbSet<SmartRouteTraffic> SmartRouteTraffic { get; set; }
        public DbSet<SmartWasteRecord> SmartWasteRecord { get; set; }
        public DbSet<SmartEnergyUsage> SmartEnergyUsage { get; set; }
        public DbSet<SmartAIInsight> SmartAIInsight { get; set; }
        public DbSet<SmartTrafficPrediction> SmartTrafficPrediction { get; set; }
        public DbSet<SmartMLModelRun> SmartMLModelRun { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // New tables — coordinates need 6 decimal places (≈ 0.1 m)
            modelBuilder.Entity<SmartTrafficPrediction>(e =>
            {
                e.Property(x => x.Latitude).HasPrecision(9, 6);
                e.Property(x => x.Longitude).HasPrecision(9, 6);
                e.Property(x => x.LocationName).HasMaxLength(200);
                e.Property(x => x.ModelUsed).HasMaxLength(20);
            });

            modelBuilder.Entity<SmartTrafficData>(e =>
            {
                e.Property(x => x.LocationName).HasMaxLength(200);
                e.Property(x => x.Source).HasMaxLength(20);
            });

            modelBuilder.Entity<SmartMLModelRun>(e =>
            {
                e.Property(x => x.ModelName).HasMaxLength(100);
                e.Property(x => x.Algorithm).HasMaxLength(100);
            });
        }
    }
}

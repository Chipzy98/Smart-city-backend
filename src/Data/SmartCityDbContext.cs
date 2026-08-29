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
    }
}
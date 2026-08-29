using Microsoft.EntityFrameworkCore;
using SmartcityBackend.src.Entities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SmartcityBackend.src.Data
{
    public class SmartCityDbContext : DbContext
    {
        public SmartCityDbContext(DbContextOptions<SmartCityDbContext> options)
            : base(options)
        {
        }

        public DbSet<SmartTrafficData> SmartTrafficData { get; set; }
        public DbSet<SmartWasteRecord> SmartWasteRecord { get; set; }
        public DbSet<SmartEnergyUsage> SmartEnergyUsage { get; set; }
        public DbSet<SmartAIInsight> SmartAIInsight { get; set; }
    }
}

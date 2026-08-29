using SmartcityBackend.src.Data;
using SmartcityBackend.src.Services.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace SmartcityBackend.src.Services
{
    public class DashboardService : IDashboardService
    {
        private readonly SmartCityDbContext db;

        public DashboardService(SmartCityDbContext db)
        {
            this.db = db;
        }

        public async Task<object> GetAdminDashboardAsync()
        {
            return new
            {
                totalTrafficRecords = await db.SmartTrafficData.CountAsync(x => x.IsActive),
                totalWasteRecords = await db.SmartWasteRecord.CountAsync(x => x.IsActive),
                totalEnergyRecords = await db.SmartEnergyUsage.CountAsync(x => x.IsActive),
                latestInsights = await db.SmartAIInsight
                    .Where(x => x.IsActive)
                    .OrderByDescending(x => x.CreatedDateTime)
                    .Take(5)
                    .ToListAsync()
            };
        }

        public async Task<object> GetManagerDashboardAsync()
        {
            return new
            {
                averageCongestion = await db.SmartTrafficData
                    .Where(x => x.IsActive)
                    .Select(x => x.CongestionLevel)
                    .DefaultIfEmpty()
                    .AverageAsync(),

                totalWaste = await db.SmartWasteRecord.CountAsync(x => x.IsActive),

                totalEnergyUsage = await db.SmartEnergyUsage
                    .Where(x => x.IsActive)
                    .Select(x => x.UsageUnits)
                    .DefaultIfEmpty()
                    .SumAsync()
            };
        }

        public async Task<object> GetUserDashboardAsync(Guid userId)
        {
            return new
            {
                myWasteRecords = await db.SmartWasteRecord.CountAsync(x => x.IsActive && x.UserId == userId),
                myCo2Saved = await db.SmartWasteRecord
                    .Where(x => x.IsActive && x.UserId == userId)
                    .Select(x => x.Co2Saved)
                    .DefaultIfEmpty()
                    .SumAsync(),

                myEnergyUsage = await db.SmartEnergyUsage
                    .Where(x => x.IsActive && x.UserId == userId)
                    .Select(x => x.UsageUnits)
                    .DefaultIfEmpty()
                    .SumAsync()
            };
        }
    }
}

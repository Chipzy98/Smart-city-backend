using SmartcityBackend.src.ViewModels;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SmartcityBackend.src.Services.Interfaces
{
    public interface IEnergyService
    {
        Task<object> GetMyEnergyUsageAsync(Guid userId, string q, int page, int pageSize);
        Task<object> PredictEnergyAsync(Guid userId, EnergyPredictionRequestVM request);
    }
}

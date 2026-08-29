using SmartcityBackend.src.ViewModels;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SmartcityBackend.src.Services.Interfaces
{
    public interface ITrafficService
    {
        Task<object> GetTrafficSummary(Guid userId, int status, string q, int page, int pageSize);
        Task<object> PredictTrafficAsync(TrafficPredictionRequestVM request);
    }
}

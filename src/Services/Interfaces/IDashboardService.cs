using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SmartcityBackend.src.Services.Interfaces
{
    public interface IDashboardService
    {
        Task<object> GetAdminDashboardAsync();
        Task<object> GetManagerDashboardAsync();
        Task<object> GetUserDashboardAsync(Guid userId);
    }
}

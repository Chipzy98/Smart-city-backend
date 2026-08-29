using SmartcityBackend.src.ViewModels;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SmartcityBackend.src.Services.Interfaces
{
    public interface IWasteService
    {
        Task<object> GetMyWasteRecordsAsync(Guid userId, string q, int page, int pageSize);
        Task<object> ClassifyWasteAsync(Guid userId, WasteClassifyRequestVM request);
    }
}

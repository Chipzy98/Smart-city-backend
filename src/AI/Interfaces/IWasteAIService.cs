using SmartcityBackend.src.AI.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SmartcityBackend.src.AI.Interfaces
{
    public interface IWasteAIService
    {
        WasteAIOutput Classify(WasteAIInput input);
    }
}

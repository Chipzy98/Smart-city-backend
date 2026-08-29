using Microsoft.AspNetCore.Authorization;
using SmartcityBackend.src.ViewModels;

namespace SmartcityBackend.src.Services.Interfaces
{
    public interface ITokenService
    {
        Task<TokenAuthorizationResult> IsAuthorizedAsync(
            string token,
            Guid taskId,
            Guid action);

        Guid? GetUserId(object data);
    }
}
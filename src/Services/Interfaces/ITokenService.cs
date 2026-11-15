using SmartcityBackend.src.ViewModels;

namespace SmartcityBackend.src.Services.Interfaces
{
    public interface ITokenService
    {
        /// <summary>Validates the JWT and checks the role against SmartCityPermissions.</summary>
        Task<TokenAuthorizationResult> IsAuthorizedAsync(
            string? token,
            Guid taskId,
            Guid action);

        Guid? GetUserId(object? data);
    }
}

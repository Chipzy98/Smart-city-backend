using SmartcityBackend.src.ViewModels;

namespace SmartcityBackend.src.Services.Interfaces
{
    public interface IAuthService
    {
        Task<AuthResponseVM?> LoginAsync(LoginRequestVM request);
        Task<AuthResponseVM?> RegisterAsync(RegisterRequestVM request);
    }
}
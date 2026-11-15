namespace SmartcityBackend.src.Services.Interfaces
{
    public interface IDashboardService
    {
        Task<object> GetAdminDashboardAsync();
        Task<object> GetManagerDashboardAsync();
        Task<object> GetUserDashboardAsync(Guid userId);

        /// <summary>Daily trend (Sri Lanka dates) for the dashboard chart.</summary>
        Task<object> GetTrendsAsync(Guid userId, string role, int days);
    }
}

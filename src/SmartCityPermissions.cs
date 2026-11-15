namespace SmartcityBackend.src
{
    /// <summary>
    /// Role permission matrix: (TaskId, Action) → roles allowed.
    /// Anything not listed here is denied.
    /// </summary>
    public static class SmartCityPermissions
    {
        public const string User = "user";
        public const string Manager = "manager";
        public const string Admin = "admin";

        private static readonly string[] AllRoles = { User, Manager, Admin };
        private static readonly string[] ManagerAndAdmin = { Manager, Admin };
        private static readonly string[] AdminOnly = { Admin };

        private static readonly Dictionary<(Guid Task, Guid Action), string[]> Matrix = new()
        {
            [(SmartCityTaskIds.SmartCityTraffic, SmartCityTaskActions.VIEW)] = AllRoles,
            [(SmartCityTaskIds.SmartCityTraffic, SmartCityTaskActions.CREATE)] = AllRoles,

            [(SmartCityTaskIds.SmartCityWaste, SmartCityTaskActions.VIEW)] = AllRoles,
            [(SmartCityTaskIds.SmartCityWaste, SmartCityTaskActions.CREATE)] = AllRoles,

            [(SmartCityTaskIds.SmartCityEnergy, SmartCityTaskActions.VIEW)] = AllRoles,
            [(SmartCityTaskIds.SmartCityEnergy, SmartCityTaskActions.CREATE)] = AllRoles,

            [(SmartCityTaskIds.SmartCityDashboard, SmartCityTaskActions.VIEW)] = AllRoles,
            [(SmartCityTaskIds.SmartCityManagerDashboard, SmartCityTaskActions.VIEW)] = ManagerAndAdmin,
            [(SmartCityTaskIds.SmartCityAdminDashboard, SmartCityTaskActions.VIEW)] = AdminOnly,

            [(SmartCityTaskIds.SmartCityAITraining, SmartCityTaskActions.VIEW)] = ManagerAndAdmin,
            [(SmartCityTaskIds.SmartCityAITraining, SmartCityTaskActions.CREATE)] = ManagerAndAdmin,

            [(SmartCityTaskIds.SmartCityClimate, SmartCityTaskActions.VIEW)] = AllRoles,
        };

        public static bool IsAllowed(Guid taskId, Guid action, string? role)
        {
            if (string.IsNullOrWhiteSpace(role)) return false;
            return Matrix.TryGetValue((taskId, action), out var roles)
                   && roles.Contains(role.ToLowerInvariant());
        }
    }
}

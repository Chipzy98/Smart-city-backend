namespace SmartcityBackend.src.ViewModels
{
    public class TokenAuthorizationResult
    {
        public bool Success { get; set; }

        public string Message { get; set; } = string.Empty;

        /// <summary>The validated JwtSecurityToken (kept for GetUserId compatibility).</summary>
        public object? Data { get; set; }

        public Guid? UserId { get; set; }

        public string Role { get; set; } = string.Empty;

        /// <summary>True when the token is valid but the role is not allowed (→ 403).</summary>
        public bool Forbidden { get; set; }
    }
}

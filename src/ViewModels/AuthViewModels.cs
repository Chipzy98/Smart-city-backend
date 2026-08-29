namespace SmartcityBackend.src.ViewModels
{
    public class LoginRequestVM
    {
        public string Email { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
    }

    public class RegisterRequestVM
    {
        public string Name { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public string Role { get; set; } = "user";
    }

    public class AuthResponseVM
    {
        public string Token { get; set; } = string.Empty;
        public UserVM User { get; set; } = new();
    }

    public class UserVM
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string Role { get; set; } = string.Empty;
    }
}
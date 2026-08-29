using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SmartcityBackend.src.Entities
{
    [Table("smart_User")]
    public class SmartUser
    {
        [Key]
        public Guid UserId { get; set; }

        public string Name { get; set; } = string.Empty;

        public string Email { get; set; } = string.Empty;

        public string PasswordHash { get; set; } = string.Empty;

        /// <summary>user | manager | admin</summary>
        public string Role { get; set; } = "user";

        public bool IsActive { get; set; } = true;

        public DateTime CreatedDateTime { get; set; }
    }
}
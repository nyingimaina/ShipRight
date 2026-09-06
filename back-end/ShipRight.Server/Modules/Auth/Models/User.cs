using Dapper.Contrib.Extensions;
using ShipRight.Database.Models;

namespace ShipRight.Modules.Auth.Models;

[Table("User")]
public class User : CompanyScopedModel
{
    public string Email { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsAdmin { get; set; }
    public int TokenVersion { get; set; } = 1;
}

using Dapper.Contrib.Extensions;
using ShipRight.Database.Models;

namespace ShipRight.Modules.Auth.Models;

[Table("RefreshToken")]
public class RefreshToken : Model
{
    public Guid UserId { get; set; }
    public string TokenHash { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }
}

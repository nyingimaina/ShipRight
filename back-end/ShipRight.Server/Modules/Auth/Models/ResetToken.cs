using Dapper.Contrib.Extensions;
using ShipRight.Database.Models;

namespace ShipRight.Modules.Auth.Models;

[Table("ResetToken")]
public class ResetToken : Model
{
    public Guid UserId { get; set; }
    public string TokenHash { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
    public DateTime? UsedAt { get; set; }
}

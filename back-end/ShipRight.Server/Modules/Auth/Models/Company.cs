using Dapper.Contrib.Extensions;
using ShipRight.Database.Models;

namespace ShipRight.Modules.Auth.Models;

[Table("Company")]
public class Company : Model
{
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
}

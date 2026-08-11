using Dapper.Contrib.Extensions;
using Jattac.Libraries.QBuilder.Attributes;
using Rocket.Libraries.DatabaseIntegrator;

namespace ShipRight.Database.Models;

public class Model : ModelBase<Guid>
{
    public DateTime Modified { get; set; }

    [ExplicitKey]
    public override Guid Id { get => base.Id; set => base.Id = value; }

    [QIgnore]
    public new bool HasNoId => base.HasNoId;
}

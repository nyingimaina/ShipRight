using ShipRight.Modules.Resources.Models;

namespace ShipRight.Modules.Resources;

/// <summary>POST body for /api/resources/aws-profiles/validate.</summary>
public sealed record AwsProfileValidateRequest(Guid? ProfileId, AwsProfileResource? Profile);
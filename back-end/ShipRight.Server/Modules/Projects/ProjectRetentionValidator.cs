namespace ShipRight.Modules.Projects;

/// <summary>
/// Validates the registry-retention and local-image-cache settings that live
/// alongside each project. Kept separate from ProjectRouter.ValidateAsync so the
/// core project validation surface stays untouched.
/// </summary>
internal static class ProjectRetentionValidator
{
    public const int MaxRetentionCount = 100;

    public static List<object> Validate(ProjectConfig p)
    {
        var errors = new List<object>();
        for (int i = 0; i < p.Services.Count; i++)
        {
            var s = p.Services[i];
            string F(string f) => $"services[{i}].{f}";

            if (s.ImageRetentionCount is < 0 or > MaxRetentionCount)
                errors.Add(ProjectRouter.Error(
                    $"Registry image retention must be between 0 and {MaxRetentionCount} (0 = keep all tags).", F("imageRetentionCount")));
            if (s.LocalImageKeepCount is < 0 or > MaxRetentionCount)
                errors.Add(ProjectRouter.Error(
                    $"Local image keep count must be between 0 and {MaxRetentionCount} (0 = delete all unused images).", F("localImageKeepCount")));
        }

        if (p.LocalCachePruneKeepGb is < 0 or > MaxRetentionCount)
            errors.Add(ProjectRouter.Error(
                $"Local build cache budget must be between 0 and {MaxRetentionCount} GB (0 = never prune the build cache).", "localCachePruneKeepGb"));

        return errors;
    }
}
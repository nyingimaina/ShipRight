using ShipRight.Modules.Resources.Models;

namespace ShipRight.Modules.Resources;

/// <summary>
/// Builds the environment-variable map used when invoking the AWS CLI.
/// Shared between <see cref="AwsEcrAuthProvider"/> and the profile validator
/// so the exact credentials a push will use are the ones that get tested.
/// </summary>
public static class AwsProfileEnvironment
{
    /// <summary>
    /// Returns the environment override for an AWS profile resource, or null when
    /// the resource has no profile binding, no explicit keys and no named profile —
    /// meaning the CLI should fall back to the machine's default credential chain.
    /// </summary>
    public static IReadOnlyDictionary<string, string>? Build(AwsProfileResource? profile)
    {
        if (profile is null)
            return null;

        var env = new Dictionary<string, string>();

        if (!string.IsNullOrEmpty(profile.DefaultRegion))
            env["AWS_DEFAULT_REGION"] = profile.DefaultRegion;

        if (profile.UsesExplicitKeys)
        {
            env["AWS_ACCESS_KEY_ID"] = profile.AccessKeyId;
            env["AWS_SECRET_ACCESS_KEY"] = profile.SecretAccessKey;
            if (!string.IsNullOrEmpty(profile.SessionToken))
                env["AWS_SESSION_TOKEN"] = profile.SessionToken;
        }
        else if (!string.IsNullOrEmpty(profile.ProfileName))
        {
            env["AWS_PROFILE"] = profile.ProfileName;
        }

        return env.Count == 0 ? null : env;
    }
}
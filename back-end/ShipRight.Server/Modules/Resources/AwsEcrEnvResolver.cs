using ShipRight.Modules.Resources.Models;
using ShipRight.Modules.Resources.Stores;

namespace ShipRight.Modules.Resources;

/// <summary>
/// Builds the AWS CLI environment override for a registry resource's bound
/// profile (named profile or explicit keys). Used by registry-retention code;
/// AwsEcrAuthProvider keeps its own private copy for token minting.
/// </summary>
internal static class AwsEcrEnvResolver
{
    public static async Task<IReadOnlyDictionary<string, string>?> ResolveAsync(
        IAwsProfileResourceStore? profileStore, DockerRegistryResource? resource)
    {
        if (resource?.AwsProfileResourceId is not Guid profileId || profileStore is null)
            return null;

        var profile = await profileStore.GetByIdAsync(profileId);
        if (profile is null)
            throw new InvalidOperationException(
                $"AWS profile resource '{profileId}' referenced by registry '{resource.Name}' no longer exists.");

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

        return env;
    }
}
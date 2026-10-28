namespace ShipRight.Modules.Setup;

/// <summary>
/// One technology stack ShipRight knows how to onboard. Adding a stack means adding a provider
/// and registering it in <see cref="SetupPlanner.CreateDefault"/>; nothing else changes.
/// </summary>
public interface IStackProvider
{
    string Id { get; }
    string DisplayName { get; }

    /// <summary>Entries this stack contributes to a newly created .gitignore.</summary>
    IReadOnlyList<string> GitIgnoreEntries { get; }

    /// <summary>Finds deployable components under <paramref name="rootPath"/>. Read-only.</summary>
    StackDetection Detect(string rootPath);

    string RenderDockerfile(DetectedComponent component);

    string RenderDockerIgnore(DetectedComponent component);
}

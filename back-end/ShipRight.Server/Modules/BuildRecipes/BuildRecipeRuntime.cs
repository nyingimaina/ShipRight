using ShipRight.Modules.Projects;
using ShipRight.Shared.ProcessRunner;

namespace ShipRight.Modules.BuildRecipes;

/// <summary>
/// Wraps the process runner the builds use. When a <c>docker build</c> is for an image whose service has a recipe, the
/// recipe's Dockerfile, target and arguments are added to the command; every other command, and every build of a service
/// without a recipe, goes through exactly as it was. Services are found by image name, and by build folder when several
/// projects use the same image. An invalid recipe, or two different recipes for one image, stops the build with a plain
/// message instead of building the wrong image.
/// </summary>
public sealed class BuildRecipeProcessRunner(IProcessRunner inner, IProjectStore projects) : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(
        string executable, string[] args, string? workingDir, Func<string, Task>? onOutput = null,
        Func<string, Task>? onError = null, CancellationToken ct = default,
        IReadOnlyDictionary<string, string>? envOverride = null, TimeSpan? timeout = null, string? stdin = null)
    {
        if (executable == "docker" && args.Length >= 2 && args[0] == "build" && ImageOf(args) is { } image)
        {
            var recipe = await RecipeForAsync(image, args[^1]);
            if (recipe is not null) args = DockerBuildRecipe.Apply(args, recipe);
        }
        return await inner.RunAsync(executable, args, workingDir, onOutput, onError, ct, envOverride, timeout, stdin);
    }

    /// <summary>The image name in <c>-t image:tag</c>; a registry port (localhost:5000/x:1) is not mistaken for the tag.</summary>
    internal static string? ImageOf(string[] args)
    {
        var i = Array.IndexOf(args, "-t");
        if (i < 0 || i + 1 >= args.Length) return null;
        var tag = args[i + 1];
        var colon = tag.LastIndexOf(':');
        return colon > tag.LastIndexOf('/') ? tag[..colon] : tag;
    }

    private async Task<BuildRecipe?> RecipeForAsync(string image, string context)
    {
        var services = (await projects.GetAllAsync())
            .SelectMany(p => p.Services)
            .Where(s => string.Equals(s.DockerImageName, image, StringComparison.Ordinal))
            .ToList();
        if (services.Count == 0) return null;

        var sameFolder = services.Where(s => SamePath(s.BuildContextPath, context)).ToList();
        var candidates = sameFolder.Count > 0 ? sameFolder : services;

        var recipes = new List<BuildRecipe>();
        foreach (var r in candidates.Select(BuildRecipe.Of).Where(r => !r.IsEmpty))
            if (!recipes.Any(x => x.SameAs(r))) recipes.Add(r);

        return recipes.Count switch
        {
            0 => null,
            1 => recipes[0],
            _ => throw new InvalidOperationException(
                $"The image '{image}' is built by more than one project with different build settings, so ShipRight cannot tell which to use. Give each project its own image name."),
        };
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(a?.TrimEnd('/', '\\').Replace('\\', '/'), b?.TrimEnd('/', '\\').Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The original project wizard rebuilds every service from a fixed list of fields, so a recipe set elsewhere would be wiped
/// by an ordinary edit. When a service arrives without one, the stored value is kept; a value that is sent (even an empty
/// one, which clears it on purpose) always wins.
/// </summary>
public sealed class BuildRecipePreservingProjectStore(IProjectStore inner) : IProjectStore
{
    public int Count => inner.Count;
    public Task<List<ProjectConfig>> GetAllAsync() => inner.GetAllAsync();
    public Task<ProjectConfig?> GetByIdAsync(string id) => inner.GetByIdAsync(id);
    public Task<ProjectConfig?> GetByNameAsync(string name) => inner.GetByNameAsync(name);
    public Task DeleteAsync(string id) => inner.DeleteAsync(id);

    public async Task SaveAsync(ProjectConfig project)
    {
        var existing = await inner.GetByIdAsync(project.Id);
        if (existing is not null && project.Services.Count > 0)
        {
            var services = project.Services.Select(s =>
            {
                var old = existing.Services.FirstOrDefault(o => o.Name.Equals(s.Name, StringComparison.OrdinalIgnoreCase));
                return old is null ? s : s with
                {
                    DockerfilePath = s.DockerfilePath ?? old.DockerfilePath,
                    BuildTarget = s.BuildTarget ?? old.BuildTarget,
                    BuildArgs = s.BuildArgs ?? old.BuildArgs,
                };
            }).ToList();
            project = project with { Services = services };
        }
        await inner.SaveAsync(project);
    }
}

public static class ServiceCollectionDecoration
{
    /// <summary>
    /// Wraps whatever is already registered for <typeparamref name="T"/> (a singleton) without touching its registration:
    /// the original is built as before and handed to <paramref name="decorate"/>.
    /// </summary>
    public static IServiceCollection DecorateSingleton<T>(this IServiceCollection services, Func<IServiceProvider, T, T> decorate) where T : class
    {
        var original = services.LastOrDefault(d => d.ServiceType == typeof(T))
                       ?? throw new InvalidOperationException($"{typeof(T).Name} is not registered, so it cannot be wrapped.");
        services.Remove(original);
        services.Add(ServiceDescriptor.Singleton(typeof(T), sp =>
        {
            var inner = (T)(original.ImplementationInstance
                            ?? original.ImplementationFactory?.Invoke(sp)
                            ?? ActivatorUtilities.CreateInstance(sp, original.ImplementationType!));
            return decorate(sp, inner);
        }));
        return services;
    }
}

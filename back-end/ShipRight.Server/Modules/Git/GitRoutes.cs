using Microsoft.Extensions.DependencyInjection;

namespace ShipRight.Modules.Git;

public sealed record GitInspectRequest(string? Url, string? Token = null);

public static class GitRoutes
{
    public static void MapGitRoutes(this WebApplication app)
    {
        app.MapPost("/api/git/inspect", async (
            GitInspectRequest body, IGitInspector inspector, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.Url))
                return Results.BadRequest(new { isError = true, message = "Paste the repository address first." });

            try
            {
                return Results.Ok(await inspector.InspectAsync(body.Url, body.Token, ct));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { isError = true, message = ex.Message });
            }
            catch (NotSupportedException ex)
            {
                return Results.BadRequest(new { isError = true, message = ex.Message });
            }
            catch (GitProviderException ex)
            {
                return Results.BadRequest(new { isError = true, message = ex.Message });
            }
        });
    }
}

public static class GitRegistration
{
    public static IServiceCollection AddGitProviders(this IServiceCollection services)
    {
        services.AddHttpClient<GitHubProvider>(client =>
        {
            client.BaseAddress = new Uri("https://api.github.com/");
            client.DefaultRequestHeaders.UserAgent.ParseAdd("ShipRight");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            client.Timeout = TimeSpan.FromSeconds(20);
        });

        services.AddSingleton<IGitProvider>(sp => sp.GetRequiredService<GitHubProvider>());
        services.AddSingleton<IGitInspector, GitInspector>();
        return services;
    }
}

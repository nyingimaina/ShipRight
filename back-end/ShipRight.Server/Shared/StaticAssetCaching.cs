using Microsoft.AspNetCore.Builder;
using Microsoft.Net.Http.Headers;

namespace ShipRight.Shared;

/// <summary>
/// Cache rules for the web app files ShipRight serves. Only the build's content-hashed files under
/// /_next/static/ may be cached for good; every other file (the pages, version.json) is revalidated each time
/// (cheap: an unchanged file answers 304), so a new release is never hidden behind an old cached page.
/// </summary>
public static class StaticAssetCaching
{
    private const string HashedAssets = "public, max-age=31536000, immutable";
    private const string AlwaysRevalidate = "no-cache";

    public static string CacheControlFor(string path) =>
        path.StartsWith("/_next/static/", StringComparison.OrdinalIgnoreCase) ? HashedAssets : AlwaysRevalidate;

    public static StaticFileOptions Options() => new()
    {
        OnPrepareResponse = context =>
            context.Context.Response.Headers[HeaderNames.CacheControl] =
                CacheControlFor(context.Context.Request.Path.Value ?? "/"),
    };

    /// <summary>
    /// For the single-page fallback (an unknown route answers with the home page). It is never cached for good,
    /// even when the missing URL looked like a hashed script, or that wrong page would be pinned for a year.
    /// </summary>
    public static StaticFileOptions FallbackOptions() => new()
    {
        OnPrepareResponse = context =>
            context.Context.Response.Headers[HeaderNames.CacheControl] = AlwaysRevalidate,
    };
}

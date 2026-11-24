using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;

namespace ShipRight.Shared;

/// <summary>
/// A statically exported Next.js site has a physical index.html for every page. The SPA fallback
/// endpoint is assigned by routing before static middleware runs, so without this rewrite a hard
/// refresh of /servers/ falls through to the root index.html and the home page redirects to /projects.
/// Rewriting known page paths clears the endpoint so the real page is served; unknown paths keep
/// the SPA fallback for client-side routes.
/// </summary>
public static class StaticPageRewrite
{
    public static IApplicationBuilder UsePageIndexRewrite(this IApplicationBuilder app, IFileProvider files) =>
        app.Use(async (context, next) =>
        {
            var path = context.Request.Path.Value;
            if (HttpMethods.IsGet(context.Request.Method) &&
                !string.IsNullOrEmpty(path) && path != "/" && !Path.HasExtension(path))
            {
                var relative = path.Trim('/') + "/index.html";
                var page = files.GetFileInfo(relative);
                if (page.Exists)
                {
                    context.Request.Path = "/" + relative;
                    context.SetEndpoint(null);
                }
            }
            await next(context);
        });
}

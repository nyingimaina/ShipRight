using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Primitives;

namespace ShipRight.Modules.Identity;

// The framework's OAuth handler builds redirect_uri from the incoming request. Behind Caddy that request is
// plain HTTP on the internal network, so Google sees http://... and rejects it with redirect_uri_mismatch.
// The public address is configured explicitly and must be used for the callback instead.
public static class GoogleCallbackUrl
{
    public const string Path = "/api/auth/google/callback";

    public static string FromEnvironment()
    {
        var publicUrl = Environment.GetEnvironmentVariable("SHIPRIGHT__PUBLIC_URL");
        return string.IsNullOrWhiteSpace(publicUrl)
            ? Path
            : $"{publicUrl.TrimEnd('/')}{Path}";
    }

    public static string ReplaceIn(string authorizationUrl, string callbackUrl)
    {
        var uri = new UriBuilder(authorizationUrl);
        var query = QueryHelpers.ParseQuery(uri.Query);
        query["redirect_uri"] = new StringValues(callbackUrl);
        uri.Query = QueryString.Create(query).Value ?? string.Empty;
        return uri.Uri.AbsoluteUri;
    }
}

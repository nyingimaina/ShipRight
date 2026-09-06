using Rocket.Libraries.DatabaseIntegrator;
using Serilog;

namespace ShipRight.Database;

public class DatabaseTransactionMiddleware
{
    private static readonly string[] ExcludedPrefixes = ["/api/builds/", "/api/health", "/api/fs/"];

    private readonly RequestDelegate _next;

    public DatabaseTransactionMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, IDatabaseHelper<Guid> databaseHelper)
    {
        var path = context.Request.Path.Value ?? "";
        var method = context.Request.Method;

        // Only mutating API requests need a DB transaction. Static SPA assets and
        // read-only requests share the singleton DatabaseHelper; a browser fires
        // them in parallel, and MySql.Data cannot nest transactions on one connection.
        var isMutating = HttpMethods.IsPost(method)
            || HttpMethods.IsPut(method)
            || HttpMethods.IsPatch(method)
            || HttpMethods.IsDelete(method);
        var isApi = path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase);
        var isExcluded = ExcludedPrefixes.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase));

        if (!isMutating || !isApi || isExcluded)
        {
            await _next(context);
            return;
        }

        try
        {
            databaseHelper.BeginTransaction();
            await _next(context);
            databaseHelper.CommitTransaction();
        }
        catch
        {
            try { databaseHelper.RollBackTransaction(); }
            catch (Exception ex) { Log.Warning(ex, "Transaction rollback failed"); }
            throw;
        }
    }
}

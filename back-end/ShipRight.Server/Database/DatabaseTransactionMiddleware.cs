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

        if (ExcludedPrefixes.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
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
